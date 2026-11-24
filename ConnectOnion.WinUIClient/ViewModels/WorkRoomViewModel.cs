using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;
using Microsoft.UI.Dispatching;

namespace ConnectOnion.WinUIClient.ViewModels;

// backend of the workroom page. loads saved workroom and its conversation, files, approvals, permissions and status to the frontend.
// routes provider messages, stops, approval responses and permission changes which are applied on the frontend.

// mainly follows and built according to what our client has provided and implemented in the web version at https://github.com/openonion/connectonion-react
public sealed partial class WorkRoomViewModel : ObservableObject
{
    private readonly ProviderWorkRoomRepository _workRooms;
    private readonly AgentRepository _agents;
    private readonly SessionRepository _sessions;
    private readonly AgentSessionManager _runtime;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private string? _workRoomId;
    private bool _disposed;

    // subscribes this backend to the frontend using the MVVM model
    public WorkRoomViewModel(
        ProviderWorkRoomRepository workRooms,
        AgentRepository agents,
        SessionRepository sessions,
        AgentSessionManager runtime)
    {
        _workRooms = workRooms;
        _agents = agents;
        _sessions = sessions;
        _runtime = runtime;
        _workRooms.WorkRoomUpdated += OnWorkRoomUpdated;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionOptions))]
    [NotifyPropertyChangedFor(nameof(SelectedPermissionId))]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyPropertyChangedFor(nameof(CanChangePermission))]
    [NotifyPropertyChangedFor(nameof(ProviderHeader))]
    [NotifyPropertyChangedFor(nameof(HasMessages))]
    [NotifyPropertyChangedFor(nameof(HasFiles))]
    [NotifyPropertyChangedFor(nameof(HasApprovals))]
    [NotifyPropertyChangedFor(nameof(HasPermission))]
    [NotifyPropertyChangedFor(nameof(ComposerUnavailableText))]
    public partial ProviderWorkRoom? Room { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial bool IsSending { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    public partial bool IsStopping { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangePermission))]
    public partial bool IsChangingPermission { get; private set; }

    [ObservableProperty]
    public partial string? PermissionStatus { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolvingApproval))]
    public partial string? ResolvingApprovalId { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public bool CanSend => !IsSending && Room?.CanContinue == true
        && !string.IsNullOrWhiteSpace(Draft);
    public bool CanStop => !IsStopping && Room?.CanStop == true;
    public bool CanContinue => Room?.CanContinue == true;
    public bool CanChangePermission => !IsChangingPermission && Room?.CanChangePermission == true;
    public IReadOnlyList<ProviderWorkRoomPermissionOption> PermissionOptions => Room?.Permission?.Options switch
    {
        { } options => options.Where(option => option.Selectable || option.Id == SelectedPermissionId).ToArray(),
        _ => [],
    };
    public string? SelectedPermissionId => Room?.Permission?.ActiveOptionId;
    public string ProviderHeader => Room switch
    {
        null => "",
        var room => $"{room.ProviderDisplayName} · {ProviderWorkRoom.StatusLabel(room.Status)}",
    };
    public bool HasMessages => Room?.Messages.Count > 0;
    public bool HasFiles => Room?.Files.Count > 0;
    public bool HasApprovals => Room?.Approvals.Count > 0;
    public bool HasPermission => Room?.Permission?.Options.Count > 0;
    public bool IsResolvingApproval => ResolvingApprovalId is not null;
    public string ComposerUnavailableText => Room switch
    {
        null => "This Workroom is unavailable.",
        { CanContinue: true } => "",
        _ => "The host does not support follow-up messages in this Workroom.",
    };

    // loads the workroom and reconnects to its parent agent session
    public async Task LoadAsync(string workRoomId)
    {
        _workRoomId = workRoomId;
        ErrorMessage = null;
        try
        {
            var room = await _workRooms.GetAsync(workRoomId);
            Room = room;
            if (room is null)
            {
                ErrorMessage = "This Workroom could not be found in the parent conversation.";
                return;
            }
            var (agent, parent) = await GetContextAsync(room);
            await _runtime.ConnectWorkRoomAsync(agent, room.ParentConversationId,
                parent.RemoteSessionId ?? parent.Id);

            Room = await _workRooms.GetAsync(workRoomId) ?? Room;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    // sends a message to the provider for this workroom
    public async Task SendAsync()
    {
        var room = Room;
        var text = Draft.Trim();

        if (room is null || !room.CanContinue || text.Length == 0 || IsSending) return;

        var clientMessageId = Guid.NewGuid().ToString("N");

        IsSending = true;
        ErrorMessage = null;
        try
        {
            var fields = new List<KeyValuePair<string, object?>>
            {
                new("workroomId", room.WorkRoomId),
                new("invocationId", room.CurrentInvocationId),
                new("observedRevision", room.StateRevision),
                new("clientMessageId", clientMessageId),
                new("text", text),
            };

            if (!string.IsNullOrWhiteSpace(room.ProviderSessionRef))
                fields.Insert(1, new("providerSessionRef", room.ProviderSessionRef));

            await SendRequestAsync(room, "PROVIDER_INPUT", fields.ToArray());
            Draft = "";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    // requests an interrupt (stop) for the currently invoked provider
    public async Task StopAsync()
    {
        var room = Room;

        if (room is null || !room.CanStop || IsStopping) return;

        IsStopping = true;
        ErrorMessage = null;
        try
        {
            await SendRequestAsync(room, "PROVIDER_INTERRUPT",
                new("workroomId", room.WorkRoomId),
                new("providerSessionRef", room.ProviderSessionRef),
                new("invocationId", room.CurrentInvocationId),
                new("observedRevision", room.StateRevision),
                new("clientRequestId", Guid.NewGuid().ToString("N")));
        }
        catch (Exception ex)
        {
            IsStopping = false;
            ErrorMessage = ex.Message;
        }
    }

    // sends an approve/reject decision back to the host
    public async Task ResolveApprovalAsync(ProviderWorkRoomApproval approval, bool approve)
    {
        var room = Room;
        if (room is null || !approval.IsPending || ResolvingApprovalId is not null) return;

        var expected = new[] { "reject", "deny" };
        if (approve) expected = ["approve", "accept", "allow"];

        var response = approval.AllowedOptions.FirstOrDefault(option =>
            expected.Contains(option, StringComparer.OrdinalIgnoreCase));

        if (response is null || string.IsNullOrWhiteSpace(room.ProviderSessionRef))
        {
            ErrorMessage = "The returned approval response was not for an advertised host request.";
            return;
        }
        ResolvingApprovalId = approval.RequestId;
        ErrorMessage = null;
        try
        {
            await SendRequestAsync(room, "PROVIDER_APPROVAL_RESPONSE",
                new("workroomId", room.WorkRoomId),
                new("providerSessionRef", room.ProviderSessionRef),
                new("invocationId", approval.InvocationId ?? room.CurrentInvocationId),
                new("requestId", approval.RequestId),
                new("observedRevision", approval.Revision),
                new("response", response));
        }
        catch (Exception ex)
        {
            ResolvingApprovalId = null;
            ErrorMessage = ex.Message;
        }
    }

    // checks for a permission change and whether it requires elevation, then asks the host to change the provider's permissions
    public async Task ChangePermissionAsync(string optionId, bool elevatedConfirmed)
    {
        var room = Room;
        var permission = room?.Permission;
        if (room is null || permission is null || !room.CanChangePermission) return;

        if (IsChangingPermission)
        {
            PermissionStatus = "A permission change is in progress and waiting for host confirmation.";
            RefreshPermissionSelection();
            return;
        }

        var option = permission.Options.FirstOrDefault(item => item.Id == optionId);
        if (option is null || !option.Selectable) return;

        var elevated = option.Risk is "high" or "danger" or "critical"
            || option.NativeProfileId == ":danger-full-access";
        if (elevated && !elevatedConfirmed) return;

        IsChangingPermission = true;
        _requestedPermissionOption = option.Id;
        PermissionStatus = "Waiting for host confirmation. The current permission level remains active.";

        ErrorMessage = null;
        try
        {
            StartPermissionChangeTimeout(room.WorkRoomId, option.Id);
            await SendRequestAsync(room, "PROVIDER_PERMISSION_CHANGE",
                new("workroomId", room.WorkRoomId),
                new("invocationId", room.CurrentInvocationId),
                new("observedRevision", permission.EffectiveRevision),
                new("optionId", option.Id),
                new("confirmRisk", elevated && elevatedConfirmed));
        }
        catch (Exception ex)
        {
            CancelPermissionChangeTimeout();
            IsChangingPermission = false;
            _requestedPermissionOption = null;
            await _workRooms.SetPermissionChangeUnavailableAsync(room.WorkRoomId, true);
            PermissionStatus = null;
            ErrorMessage = ex.Message;
            OnPropertyChanged(nameof(SelectedPermissionId));
        }
    }

    // if the host rejects permission change then restore the property to the last approved permission level
    public void RefreshPermissionSelection() => OnPropertyChanged(nameof(SelectedPermissionId));

    // cancels permission changes and stops workroom page updates if the workroom page is closed
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPermissionChangeTimeout();
        _workRooms.WorkRoomUpdated -= OnWorkRoomUpdated;
    }

    // loads the agent configuration and parent session for the workroom if possible
    private async Task<(AgentConfig Agent, SessionSummary Parent)> GetContextAsync(ProviderWorkRoom room)
    {
        const string missingContext = "The parent agent or conversation is no longer available.";
        var parent = await _sessions.GetSessionAsync(room.ParentConversationId) ?? throw new InvalidOperationException(missingContext);
        var agent = await _agents.GetAgentAsync(parent.AgentId) ?? throw new InvalidOperationException(missingContext);
        return (agent, parent);
    }

    // connects to the parent session for the workroom and sends a provider request
    private async Task SendRequestAsync(ProviderWorkRoom room, string type, params KeyValuePair<string, object?>[] fields)
    {
        var (agent, parent) = await GetContextAsync(room);
        await _runtime.SendWorkRoomRequestAsync(
            agent, room, parent.RemoteSessionId ?? parent.Id, type, fields);
    }

    // applies updates for the current workroom's permissions, approvals and stop indicators
    private void OnWorkRoomUpdated(ProviderWorkRoom updated)
    {
        if (!string.Equals(updated.WorkRoomId, _workRoomId, StringComparison.Ordinal)) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;

            var previousPermissionRevision = Room?.Permission?.EffectiveRevision ?? 0;
            var previousPermissionAckCounter = Room?.PermissionAckCounter ?? 0;
            Room = updated;

            var permissionRevisionAdvanced = updated.Permission?.EffectiveRevision > previousPermissionRevision;
            var permissionAckAdvanced = updated.PermissionAckCounter > previousPermissionAckCounter
                && (updated.PermissionAckOptionId is null
                    || updated.PermissionAckOptionId == _requestedPermissionOption);

            var permissionChangeSettled = permissionRevisionAdvanced || permissionAckAdvanced || updated.PermissionChangeUnavailable;
            if (IsChangingPermission && PermissionStatus is not null && permissionChangeSettled)
            {
                CancelPermissionChangeTimeout();
                IsChangingPermission = false;
                if (permissionRevisionAdvanced && updated.Permission?.ActiveOptionId == _requestedPermissionOption)
                    PermissionStatus = "The provider's permission level has been updated.";
                else PermissionStatus = updated.PermissionAckError ?? "The host did not apply the permission change.";
                _requestedPermissionOption = null;
            }

            if (ResolvingApprovalId is { } approvalId && updated.Approvals.Any(approval => approval.RequestId == approvalId && !approval.IsPending))
            {
                ResolvingApprovalId = null;
            }

            if (updated.Status is "completed" or "failed" or "cancelled") IsStopping = false;
        });
    }

    private string? _requestedPermissionOption;
    private CancellationTokenSource? _permissionChangeTimeout;

    // stops older permission change timers and starts a new one for the latest change
    private void StartPermissionChangeTimeout(string workRoomId, string requestedOptionId)
    {
        CancelPermissionChangeTimeout();
        var timeout = new CancellationTokenSource();
        _permissionChangeTimeout = timeout;
        _ = ReleaseUnconfirmedPermissionChangeAsync(workRoomId, requestedOptionId, timeout.Token);
    }

    // confirms unsuccessful permission change and restores the old permission level if the host doesn't confirm the change
    private async Task ReleaseUnconfirmedPermissionChangeAsync(
        string workRoomId, string requestedOptionId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            await _workRooms.SetPermissionChangeUnavailableAsync(
                workRoomId, true, cancellationToken).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || !IsChangingPermission || _workRoomId != workRoomId || _requestedPermissionOption != requestedOptionId) return;

                IsChangingPermission = false;
                _requestedPermissionOption = null;
                PermissionStatus = "The host did not confirm the permission change. The current permission level remains active.";
                OnPropertyChanged(nameof(SelectedPermissionId));
            });
        }
        catch (OperationCanceledException) { }
    }

    // stops older permission change timer
    private void CancelPermissionChangeTimeout()
    {
        var timeout = _permissionChangeTimeout;
        _permissionChangeTimeout = null;
        if (timeout is null) return;
        timeout.Cancel();
        timeout.Dispose();
    }

}
