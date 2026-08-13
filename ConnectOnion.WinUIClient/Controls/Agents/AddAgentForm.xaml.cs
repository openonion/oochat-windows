using System;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Window-level Add Agent overlay. Network, validation, and persistence state live in
/// <see cref="AddAgentViewModel"/>; this class owns modal lifecycle, input, and the optional
/// agent icon — picking it, previewing it, and cleaning it up if the agent is never created.
/// </summary>
public sealed partial class AddAgentForm : UserControl, IDisposable
{
    private CancellationTokenSource? _operationCts;
    private FrameworkElement? _focusReturnTarget;
    private bool _initialFocusPending;
    private int _disposed;

    /// <summary>The processed-but-uncommitted icon. It lives in the temporary directory until the
    /// agent it belongs to exists, and is deleted on every path that closes the form without
    /// creating one.</summary>
    private string? _temporaryIconPath;

    /// <summary>Set by the commit hook so a failed save can delete the file it just wrote.</summary>
    private string? _committedIconPath;

    /// <summary>Guards the two icon buttons against re-entry while a picker is open.</summary>
    private int _iconBusy;

    public AddAgentViewModel Vm { get; } = App.GetService<AddAgentViewModel>();

    public event Action<AgentConfig>? AgentAdded;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Exposes this overlay to UI Automation as a dialog. Without a peer the control is
    /// invisible to UIA entirely — no dialog boundary for a screen reader, and its
    /// AutomationId unreachable from a UI test. See <see cref="ModalOverlayAutomationPeer"/>.</summary>
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => new ModalOverlayAutomationPeer(this);


    public AddAgentForm()
    {
        InitializeComponent();
        Loaded += AddAgentForm_Loaded;
        SizeChanged += (_, _) => UpdateModalHeight();
    }

    private void UpdateModalHeight()
    {
        // Keep the centered card inside the viewport. The ScrollViewer then owns overflow caused
        // by large text, a short window, or the expanded appearance section.
        ModalContainer.MaxHeight = Math.Min(720, Math.Max(240, ActualHeight - 48));
    }

    public string LocalizedTestButtonText(bool isTesting)
        => isTesting
            ? LocalizedStrings.Get("AddAgentTesting", "Testing…")
            : LocalizedStrings.Get("AddAgentTestConnection", "Test connection");

    public string LocalizedAddButtonText(bool isAdding)
        => isAdding
            ? LocalizedStrings.Get("AddAgentAdding", "Adding…")
            : LocalizedStrings.Get("AddAgentSubmit", "Add agent");

    public string LocalizedInputHelpText(bool showValidationError)
        => showValidationError
            ? LocalizedStrings.Get(
                "AddAgentValidationError",
                "Enter a valid agent address or HTTP URL.")
            : "";

    public string LocalizedStatusText(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "";
        if (status.StartsWith("Connected to ", StringComparison.Ordinal))
        {
            return LocalizedStrings.Format(
                "AddAgentConnectedTo",
                "Connected to {0}",
                status["Connected to ".Length..]);
        }
        if (status.StartsWith("Could not add the agent: ", StringComparison.Ordinal))
        {
            return LocalizedStrings.Format(
                "AddAgentCouldNotAdd",
                "Could not add the agent: {0}",
                status["Could not add the agent: ".Length..]);
        }

        return status switch
        {
            "Checking agent health…" => LocalizedStrings.Get(
                "AddAgentCheckingHealth",
                "Checking agent health…"),
            "An agent with this address and Direct URL already exists." => LocalizedStrings.Get(
                "AddAgentDuplicateAddressAndUrl",
                "An agent with this address and Direct URL already exists."),
            "An agent with this address already exists." => LocalizedStrings.Get(
                "AddAgentDuplicateAddress",
                "An agent with this address already exists."),
            "An agent with this Direct URL already exists." => LocalizedStrings.Get(
                "AddAgentDuplicateUrl",
                "An agent with this Direct URL already exists."),
            "Connection timed out." => LocalizedStrings.Get(
                "AddAgentTimeout",
                "Connection timed out."),
            "The agent reported an unhealthy state." => LocalizedStrings.Get(
                "AddAgentUnhealthy",
                "The agent reported an unhealthy state."),
            "The server did not return valid agent information." => LocalizedStrings.Get(
                "AddAgentInvalidInfo",
                "The server did not return valid agent information."),
            "Could not connect to the agent." => LocalizedStrings.Get(
                "AddAgentCouldNotConnect",
                "Could not connect to the agent."),
            _ => status,
        };
    }

    public void Show(FrameworkElement? focusReturnTarget)
    {
        ThrowIfDisposed();
        _focusReturnTarget = focusReturnTarget;

        if (!IsOpen)
        {
            CancelPendingOperation();
            Vm.Reset();
            ResetIcon();
            _operationCts = new CancellationTokenSource();
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
        }

        QueueInitialFocus();
    }

    public void Hide()
    {
        if (!IsOpen) return;

        CancelPendingOperation();
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        Vm.Reset();
        ResetIcon();
        _initialFocusPending = false;

        _focusReturnTarget?.Focus(FocusState.Programmatic);
        _focusReturnTarget = null;
    }

    private void AddAgentForm_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialFocusPending) QueueInitialFocus();
    }

    private void QueueInitialFocus()
    {
        _initialFocusPending = true;
        if (!IsLoaded) return;

        // A lazily-created overlay becomes visible before its first measure/arrange pass. A normal
        // priority callback can therefore call Focus while the TextBox still has no focusable
        // presentation source. Run after layout and retry once if WinUI reports that exact race.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (!IsOpen)
                {
                    _initialFocusPending = false;
                    return;
                }

                _initialFocusPending = !InputBox.Focus(FocusState.Programmatic);
                if (!_initialFocusPending) return;

                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (IsOpen) InputBox.Focus(FocusState.Programmatic);
                        _initialFocusPending = false;
                    });
            });
    }

    public void CancelPendingOperation()
    {
        var cts = Interlocked.Exchange(ref _operationCts, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    public void Shutdown() => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelPendingOperation();
        DiscardTemporaryIcon();
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _focusReturnTarget = null;
        _initialFocusPending = false;
        AgentAdded = null;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
        => await TestConnectionAsync();

    private async Task TestConnectionAsync()
    {
        var token = _operationCts?.Token ?? CancellationToken.None;
        await Vm.TestConnectionAsync(token);
    }

    private async void AddAgent_Click(object sender, RoutedEventArgs e)
        => await AddAgentAsync();

    private async Task AddAgentAsync()
    {
        if (!Vm.CanAdd || !TryBeginIconOperation()) return;
        var token = _operationCts?.Token ?? CancellationToken.None;

        // The icon can't be changed mid-create: the commit hook below reads _temporaryIconPath.
        SetIconControlsBusy(true, showProgress: false);
        try
        {
            _committedIconPath = null;
            var agent = await Vm.AddAsync(token, CommitIconForNewAgentAsync);
            if (agent is null || token.IsCancellationRequested)
            {
                // AddAsync swallows its own failures and returns null, so a file the hook already
                // committed would otherwise be left with nothing referencing it.
                await DiscardCommittedIconAsync();
                return;
            }

            AgentAdded?.Invoke(agent);
            Hide();
        }
        finally
        {
            EndIconOperation();
            if (Volatile.Read(ref _disposed) == 0 && IsOpen) SetIconControlsBusy(false, showProgress: false);
        }
    }

    // ---- Agent icon ----

    private async void ChooseIcon_Click(object sender, RoutedEventArgs e) => await ChooseIconAsync();

    private async Task ChooseIconAsync()
    {
        if (Vm.IsBusy || !TryBeginIconOperation()) return;

        HideIconError();
        SetIconControlsBusy(true, showProgress: true);
        var token = _operationCts?.Token ?? CancellationToken.None;
        string? newTemporaryPath = null;

        try
        {
            newTemporaryPath = await AppServices.AgentIcons.PickTemporaryIconAsync(token);
            // Dismissing the picker is not a failure and leaves the previous pick in place.
            if (newTemporaryPath is null) return;

            // The form can be closed while the picker is up, and its cleanup has already run.
            if (token.IsCancellationRequested || !IsOpen)
            {
                await DeleteTemporaryIconAsync(newTemporaryPath);
                return;
            }

            var previousTemporaryPath = Interlocked.Exchange(ref _temporaryIconPath, newTemporaryPath);
            newTemporaryPath = null;

            AvatarPreview.ImagePath = _temporaryIconPath;
            RemoveIconButton.Visibility = Visibility.Visible;

            // Replacing a pick leaves the earlier file with nothing pointing at it.
            await DeleteTemporaryIconAsync(previousTemporaryPath);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await DeleteTemporaryIconAsync(newTemporaryPath);
        }
        catch (AgentIconException exception)
        {
            ShowIconError(DescribeIconError(exception.Error));
        }
        catch
        {
            ShowIconError(LocalizedStrings.Get(
                "AgentIconProcessFailed", "The selected image could not be processed."));
        }
        finally
        {
            EndIconOperation();
            if (Volatile.Read(ref _disposed) == 0) SetIconControlsBusy(false, showProgress: false);
        }
    }

    private async void RemoveIcon_Click(object sender, RoutedEventArgs e) => await RemoveIconAsync();

    private async Task RemoveIconAsync()
    {
        if (Vm.IsBusy || !TryBeginIconOperation()) return;

        HideIconError();
        try
        {
            var temporaryPath = Interlocked.Exchange(ref _temporaryIconPath, null);
            ClearIconPreview();
            await DeleteTemporaryIconAsync(temporaryPath);
        }
        finally
        {
            EndIconOperation();
        }
    }

    /// <summary>
    /// Hands the picked icon to the agent being created. Runs inside
    /// <see cref="AddAgentViewModel.AddAsync"/> so the path is written in the same save as the
    /// agent itself; see that method's remarks for why it is a callback.
    /// </summary>
    private async Task<string?> CommitIconForNewAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        var temporaryPath = Volatile.Read(ref _temporaryIconPath);
        if (string.IsNullOrWhiteSpace(temporaryPath)) return null;

        try
        {
            var committedPath = await AppServices.AgentIcons.CommitTemporaryIconAsync(
                agentId, temporaryPath, cancellationToken);

            // The commit moved the file, so the temporary cleanup paths must forget it.
            Interlocked.CompareExchange(ref _temporaryIconPath, null, temporaryPath);
            _committedIconPath = committedPath;
            return committedPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentIconException exception)
        {
            // The agent is still worth creating; it just keeps its initial avatar.
            ShowIconError(DescribeIconError(exception.Error));
            return null;
        }
        catch
        {
            ShowIconError(LocalizedStrings.Get(
                "AgentIconSaveAfterAddFailed", "The agent was added, but its icon could not be saved."));
            return null;
        }
    }

    private bool TryBeginIconOperation() => Interlocked.CompareExchange(ref _iconBusy, 1, 0) == 0;

    private void EndIconOperation() => Volatile.Write(ref _iconBusy, 0);

    private void SetIconControlsBusy(bool isBusy, bool showProgress)
    {
        ChooseIconButton.IsEnabled = !isBusy;
        RemoveIconButton.IsEnabled = !isBusy;
        IconProgressRing.IsActive = isBusy && showProgress;
        IconProgressRing.Visibility = isBusy && showProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Returns the form to "no icon chosen" and deletes whatever was pending.</summary>
    private void ResetIcon()
    {
        DiscardTemporaryIcon();
        _committedIconPath = null;
        EndIconOperation();
        ClearIconPreview();
        HideIconError();
        SetIconControlsBusy(false, showProgress: false);
    }

    private void ClearIconPreview()
    {
        AvatarPreview.ImagePath = null;
        RemoveIconButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Fire-and-forget deletion for the synchronous lifecycle methods, which cannot await.</summary>
    private void DiscardTemporaryIcon()
    {
        var temporaryPath = Interlocked.Exchange(ref _temporaryIconPath, null);
        if (!string.IsNullOrWhiteSpace(temporaryPath)) _ = DeleteTemporaryIconAsync(temporaryPath);
    }

    private async Task DiscardCommittedIconAsync()
    {
        var committedPath = Interlocked.Exchange(ref _committedIconPath, null);
        if (string.IsNullOrWhiteSpace(committedPath)) return;

        try
        {
            await AppServices.AgentIcons.DeleteIconAsync(committedPath);
        }
        catch
        {
            // Nothing references the file; an orphan is the acceptable outcome here.
        }
    }

    private static async Task DeleteTemporaryIconAsync(string? temporaryPath)
    {
        if (string.IsNullOrWhiteSpace(temporaryPath)) return;

        try
        {
            await AppServices.AgentIcons.DeleteTemporaryIconAsync(temporaryPath);
        }
        catch
        {
            // Cleanup must never break closing the modal or shutting the app down; the startup
            // sweep in AppStorage.PurgeTemporaryAgentIcons picks up whatever is left behind.
        }
    }

    private void ShowIconError(string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        IconErrorText.Text = message;
        IconErrorText.Visibility = Visibility.Visible;
    }

    private void HideIconError()
    {
        IconErrorText.Text = string.Empty;
        IconErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Maps a failure code to text. The service raises codes rather than messages so the
    /// wording lives here, next to the rest of this form's UI strings.</summary>
    private static string DescribeIconError(AgentIconError error) => error switch
    {
        AgentIconError.WindowUnavailable or AgentIconError.PickerFailed =>
            LocalizedStrings.Get("AgentIconPickerFailed", "The image picker could not be opened."),
        AgentIconError.UnsupportedFileType =>
            LocalizedStrings.Get("AgentIconUnsupportedType", "Choose a PNG, JPG, JPEG or WebP image."),
        AgentIconError.FileTooLarge =>
            LocalizedStrings.Get("AgentIconFileTooLarge", "The image must be 5 MB or smaller."),
        AgentIconError.ImageTooLarge =>
            LocalizedStrings.Get("AgentIconImageTooLarge", "The image's dimensions are too large."),
        AgentIconError.InvalidImage =>
            LocalizedStrings.Get("AgentIconInvalidImage", "That file is not an image this app can read."),
        AgentIconError.DeleteFailed =>
            LocalizedStrings.Get("AgentIconDeleteFailed", "The agent icon could not be removed."),
        _ =>
            LocalizedStrings.Get("AgentIconSaveFailed", "The agent icon could not be saved."),
    };

    /// <summary>
    /// "Where do I get an agent address?" — points at the host() docs so a first-run user can
    /// produce a 0x / HTTP endpoint. Reuses the icon error line to report a launch failure rather
    /// than doing nothing, on the same reasoning as everywhere else: silence here strands the user.
    /// </summary>
    private async void AddAgentDocs_Click(object sender, RoutedEventArgs e)
    {
        var docsUri = new Uri(HostDocsUrl);
        var launched = await AppServices.UriLauncher.LaunchAsync(docsUri);
        if (launched) return;

        ShowIconError(LocalizedStrings.Format(
            "AddAgentDocsOpenFailed",
            "Couldn't open the docs. Visit {0} in your browser.",
            HostDocsUrl));
    }

    private const string HostDocsUrl = "https://docs.connectonion.com/host";

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void InputBox_LostFocus(object sender, RoutedEventArgs e)
        => Vm.MarkInteracted();

    private async void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || Vm.IsBusy || Volatile.Read(ref _iconBusy) != 0) return;
        e.Handled = true;
        if (Vm.CanAdd)
            await AddAgentAsync();
        else
            await TestConnectionAsync();
    }

    private void OverlayRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        Hide();
    }

    private void Backdrop_Tapped(object sender, TappedRoutedEventArgs e) => Hide();

    private void ModalContainer_Tapped(object sender, TappedRoutedEventArgs e)
        => e.Handled = true;

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
