using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.ViewModels;

public enum AddAgentConnectionState
{
    None,
    Testing,
    Connected,
    Error,
}

/// <summary>
/// Owns validation, connection testing, duplicate detection, and persistence for the
/// transient Add Agent dialog. The view only bridges focus, keyboard input, and closing.
/// </summary>
public sealed partial class AddAgentViewModel : Common.ObservableObject
{
    public const string ValidationError = "Enter a valid agent address or HTTP URL.";

    private readonly AgentRepository _agents;
    private readonly ConnectionTester _connectionTester;
    private string? _testedInput;
    private string? _detectedName;
    private long _generation;
    private bool _resetting;

    public AddAgentViewModel(AgentRepository agents, ConnectionTester connectionTester)
    {
        _agents = agents;
        _connectionTester = connectionTester;
        _resetting = true;
        Input = "";
        StatusText = "";
        _resetting = false;
    }

    [ObservableProperty]
    public partial string Input { get; set; }

    [ObservableProperty]
    public partial bool HasInteracted { get; private set; }

    [ObservableProperty]
    public partial AddAgentConnectionState ConnectionState { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; }

    [ObservableProperty]
    public partial bool IsAdding { get; private set; }

    public bool IsTesting => ConnectionState == AddAgentConnectionState.Testing;
    public bool IsBusy => IsTesting || IsAdding;
    public bool IsInputEnabled => !IsBusy;
    public bool IsInputValid => TryParseInput(Input, out _, out _);
    public bool ShowValidationError
        => HasInteracted && !string.IsNullOrWhiteSpace(Input) && !IsInputValid;
    public string InputHelpText => ShowValidationError ? ValidationError : "";
    public bool CanTest => IsInputValid && !IsBusy;
    public bool CanAdd
        => IsInputValid && !IsBusy &&
           ConnectionState == AddAgentConnectionState.Connected &&
           string.Equals(_testedInput, Input.Trim(), StringComparison.Ordinal);
    public string TestButtonText => IsTesting ? "Testing…" : "Test connection";
    public string AddButtonText => IsAdding ? "Adding…" : "Add agent";
    public bool ShowConnected => ConnectionState == AddAgentConnectionState.Connected;
    public bool ShowConnectionError => ConnectionState == AddAgentConnectionState.Error;
    public bool ShowTestingStatus => ConnectionState == AddAgentConnectionState.Testing;

    partial void OnInputChanged(string value)
    {
        if (_resetting) return;
        HasInteracted = true;
        _generation++;
        _testedInput = null;
        _detectedName = null;
        ConnectionState = AddAgentConnectionState.None;
        StatusText = "";
        RaiseAllStateProperties();
    }

    public void MarkInteracted()
    {
        HasInteracted = true;
        RaiseValidationProperties();
    }

    public void Reset()
    {
        _generation++;
        _resetting = true;
        Input = "";
        _resetting = false;
        HasInteracted = false;
        ConnectionState = AddAgentConnectionState.None;
        StatusText = "";
        IsAdding = false;
        _testedInput = null;
        _detectedName = null;
        RaiseAllStateProperties();
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        MarkInteracted();
        if (!TryParseInput(Input, out var address, out var directUrl))
        {
            RaiseAllStateProperties();
            return;
        }

        var input = Input.Trim();
        var generation = ++_generation;
        ConnectionState = AddAgentConnectionState.Testing;
        StatusText = "Checking agent health…";
        RaiseAllStateProperties();

        try
        {
            var agentsState = await _agents.LoadAsync(cancellationToken);
            var duplicate = AgentEndpointDuplicateDetector.Find(
                agentsState.Agents, address, directUrl);
            if (duplicate != AgentEndpointDuplicate.None)
            {
                PublishError(generation, DuplicateMessage(duplicate));
                return;
            }

            var result = await _connectionTester.TestAsync(
                    string.IsNullOrWhiteSpace(address) ? null : address,
                    string.IsNullOrWhiteSpace(directUrl) ? null : directUrl,
                    cancellationToken: cancellationToken);
            if (generation != _generation || cancellationToken.IsCancellationRequested) return;

            if (!result.Ok)
            {
                PublishError(generation, SimplifyConnectionError(result.Detail));
                return;
            }

            _testedInput = input;
            _detectedName = result.AgentName;
            ConnectionState = AddAgentConnectionState.Connected;
            var name = string.IsNullOrWhiteSpace(result.AgentName)
                ? "Agent"
                : Common.FriendlyAgentName.From(result.AgentName);
            StatusText = $"Connected to {name}";
            RaiseAllStateProperties();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dialog closure intentionally abandons this result.
        }
        catch (Exception ex)
        {
            PublishError(generation, SimplifyConnectionError(ex.Message));
        }
    }

    /// <param name="commitIcon">
    /// Optional hook invoked with the new agent's id once it exists but before anything is
    /// written, returning the relative icon path to store (or null for none). It takes this shape
    /// because the icon is committed under a filename derived from an id that only exists here,
    /// yet the result has to land in the <i>same</i> save as the agent — a second write would open
    /// a window where a concurrent agent-list save silently drops the icon. Keeping it a delegate
    /// also keeps this view model free of the picker and file APIs, which are WinUI-only.
    ///
    /// <para>The hook owns its own failures: returning null means "no icon", and the agent is
    /// still created. An icon is optional decoration and must not cost the user the agent they
    /// were adding.</para>
    /// </param>
    public async Task<AgentConfig?> AddAsync(
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<string?>>? commitIcon = null)
    {
        if (!CanAdd || IsAdding ||
            !TryParseInput(Input, out var address, out var directUrl))
        {
            return null;
        }

        var generation = _generation;
        IsAdding = true;
        RaiseAllStateProperties();

        try
        {
            var agentsState = await _agents.LoadAsync(cancellationToken);
            var duplicate = AgentEndpointDuplicateDetector.Find(
                agentsState.Agents, address, directUrl);
            if (duplicate != AgentEndpointDuplicate.None)
            {
                PublishError(generation, DuplicateMessage(duplicate));
                return null;
            }

            var input = Input.Trim();
            var agent = new AgentConfig
            {
                Id = Guid.NewGuid().ToString(),
                Name = _detectedName ?? CreateFallbackName(input),
                Address = address,
                DirectUrl = string.IsNullOrWhiteSpace(directUrl) ? null : directUrl,
            };

            if (commitIcon is not null)
            {
                agent.IconPath = await commitIcon(agent.Id, cancellationToken);
            }

            if (!await _agents.AppendAgentAsync(agent, makeSelected: true, cancellationToken))
                throw new InvalidOperationException("The agent could not be inserted.");
            return agent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            PublishError(generation, $"Could not add the agent: {ex.Message}");
            return null;
        }
        finally
        {
            if (generation == _generation)
            {
                IsAdding = false;
                RaiseAllStateProperties();
            }
        }
    }

    private void PublishError(long generation, string message)
    {
        if (generation != _generation) return;
        _testedInput = null;
        _detectedName = null;
        ConnectionState = AddAgentConnectionState.Error;
        StatusText = message;
        RaiseAllStateProperties();
    }

    private void RaiseAllStateProperties()
    {
        RaiseValidationProperties();
        OnPropertyChanged(nameof(ConnectionState));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsAdding));
        OnPropertyChanged(nameof(IsTesting));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsInputEnabled));
        OnPropertyChanged(nameof(CanTest));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(TestButtonText));
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(ShowConnected));
        OnPropertyChanged(nameof(ShowConnectionError));
        OnPropertyChanged(nameof(ShowTestingStatus));
    }

    private void RaiseValidationProperties()
    {
        OnPropertyChanged(nameof(IsInputValid));
        OnPropertyChanged(nameof(ShowValidationError));
        OnPropertyChanged(nameof(InputHelpText));
    }

    private static bool TryParseInput(string? value, out string address, out string directUrl)
    {
        var input = value?.Trim() ?? "";
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            address = input;
            directUrl = "";
            return AgentAddressValidator.IsValid(input);
        }

        address = "";
        directUrl = input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static string DuplicateMessage(AgentEndpointDuplicate duplicate) => duplicate switch
    {
        AgentEndpointDuplicate.Address | AgentEndpointDuplicate.DirectUrl
            => "An agent with this address and Direct URL already exists.",
        AgentEndpointDuplicate.Address => "An agent with this address already exists.",
        _ => "An agent with this Direct URL already exists.",
    };

    private static string SimplifyConnectionError(string detail)
    {
        if (detail.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "Connection timed out.";
        if (detail.Contains("HTTP", StringComparison.OrdinalIgnoreCase))
            return detail;
        if (detail.Contains("unhealthy", StringComparison.OrdinalIgnoreCase))
            return "The agent reported an unhealthy state.";
        if (detail.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            return "The server did not return valid agent information.";
        return string.IsNullOrWhiteSpace(detail)
            ? "Could not connect to the agent."
            : detail;
    }

    internal static string CreateFallbackName(string input)
        => string.IsNullOrWhiteSpace(input)
            ? "Agent"
            : $"Agent {input[..Math.Min(8, input.Length)]}";
}
