using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectOnion.WinUIClient.Models;

// this file holds the state for one instance of a provider's workroom, including its conversation, files, approvals, capabilities and permission level
// applies the provider event and rejects mismatched states

// mainly follows and built according to what our client has provided and implemented in the web version at https://github.com/openonion/connectonion-react
// the cases are defined in https://github.com/openonion/connectonion/issues/1109 and the project spec
public sealed class ProviderWorkRoom
{
    public const int MaxMessages = 200;
    public string WorkRoomId { get; set; } = "";
    public string ParentConversationId { get; set; } = "";
    public string Provider { get; set; } = "unknown";
    public string ProviderDisplayName { get; set; } = "Coding agent";
    public string? ProviderSessionRef { get; set; }
    public string? CurrentInvocationId { get; set; }
    public string? ParentToolCallId { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public string Title { get; set; } = "Coding work";
    public string Status { get; set; } = "starting";
    public long StateRevision { get; set; }
    public bool HasAuthoritativeState { get; set; }
    public bool PermissionChangeUnavailable { get; set; }
    public long PermissionAckCounter { get; set; }
    public bool? PermissionAckAccepted { get; set; }
    public string? PermissionAckOptionId { get; set; }
    public string? PermissionAckError { get; set; }
    public List<string> AppliedPermissionAckIds { get; set; } = [];
    public ProviderWorkRoomPermission? Permission { get; set; }
    public List<ProviderWorkRoomMessage> Messages { get; set; } = [];
    public List<ProviderWorkRoomFile> Files { get; set; } = [];
    public List<ProviderWorkRoomApproval> Approvals { get; set; } = [];

    public bool IsRunning => Status is "starting" or "running" or "thinking" or "working"
        or "awaiting_approval" or "awaiting_input" or "stopping";
    public bool CanContinue => HasAuthoritativeState && StateRevision > 0
        && HasCapability("provider_continuation", "provider_input") && HasProviderTarget
        && Status is not ("failed" or "cancelled" or "unavailable");
    public bool CanStop => HasAuthoritativeState && IsRunning && HasCapability("provider_interrupt");
    public bool CanChangePermission => HasAuthoritativeState
        && Permission is { Options.Count: > 0 }
        && Permission.EffectiveRevision > 0
        && Permission.UnavailableSummary is null
        && Permission.Options.Any(option => option.Selectable)
        && HasCapability("provider_permission_change")
        && !PermissionChangeUnavailable
        && PermissionAckAccepted != false
        && !string.IsNullOrWhiteSpace(CurrentInvocationId);
    private bool HasProviderTarget => !string.IsNullOrWhiteSpace(ProviderSessionRef)
        || !string.IsNullOrWhiteSpace(CurrentInvocationId);

    // checks if a string is a workroom event
    public static bool IsWorkRoomEvent(string type) => type is
        "provider_session" or "provider_invocation" or "provider_activity" or "provider_approval" or
        "provider_artifact" or "provider_continuation" or "provider_input_ack" or "provider_interrupt" or
        "provider_error" or "provider_message" or "provider_phase" or "provider_permission_ack";

    // reads workrooms length and binds its length
    public static string? GetExplicitWorkRoomId(JsonElement root)
        => Bounded(Text(root, "workroomId"), 128);

    // reads client message id to match provider input later on and binds its length
    public static string? GetClientMessageId(JsonElement root)
        => Bounded(Text(root, "clientMessageId"), 128);

    // if valid provider event, apply the fields for the workroom, else, reject them
    internal bool Apply(string type, JsonElement root, string? correlatedWorkRoomId = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;

        var workroomId = GetExplicitWorkRoomId(root) ?? Bounded(correlatedWorkRoomId, 128);
        if (!ValidIdentifier(workroomId, 128)) return false;

        if (WorkRoomId.Length != 0 && !string.Equals(WorkRoomId, workroomId, StringComparison.Ordinal))
            return false;

        WorkRoomId = workroomId!;
        Provider = Bounded(Text(root, "provider"), 48) ?? Provider;
        ProviderDisplayName = Bounded(Text(root, "displayName"), 48) ?? ProviderDisplayName;

        if (ProviderDisplayName == "Coding agent") ProviderDisplayName = ProviderLabel(Provider);

        var authorityEvent = IsAuthorityEvent(type);
        var revision = Number(root, "stateRevision");

        if (revision is null && authorityEvent && Object(root, "providerPermission") is { } revisionPermission)
            revision = Number(revisionPermission, "effectiveRevision");

        if (revision is >= 0 && authorityEvent)
        {
            if (HasAuthoritativeState && revision.Value < StateRevision) return false;
            StateRevision = Math.Max(StateRevision, revision.Value);
            HasAuthoritativeState = true;
        }

        if (type is "provider_session" or "provider_invocation")
        {
            ProviderSessionRef = Bounded(Text(root, "providerSessionRef"), 256) ?? ProviderSessionRef;
            Status = NormalizeStatus(Text(root, "status") ?? Status);
            if (root.TryGetProperty("capabilities", out _)) Capabilities = ReadStrings(root, "capabilities", 24, 64);
        }

        switch (type)
        {
            case "provider_session":
                ParentConversationId = Bounded(Text(root, "parentSessionId"), 128) ?? ParentConversationId;
                break;

            case "provider_invocation":
                CurrentInvocationId = Bounded(Text(root, "invocationId"), 128) ?? CurrentInvocationId;
                ParentToolCallId = Bounded(Text(root, "parentToolCallId"), 128) ?? ParentToolCallId;
                Title = Bounded(Text(root, "title"), 200) ?? Title;
                if (Object(root, "providerPermission") is { } permission)
                    ApplyPermissionSnapshot(permission, revision);
                break;

            case "provider_activity":
                ApplyFiles(root, "activity");
                break;

            case "provider_message":
                ApplyMessage(root);
                break;

            case "provider_phase":
                Status = NormalizeStatus(Text(root, "phase") ?? Status);
                break;

            case "provider_approval":
                ApplyApproval(root);
                break;

            case "provider_continuation":
                ApplyContinuation(root);
                break;

            case "provider_input_ack":
                ApplyContinuation(root, "acknowledged");
                break;

            case "provider_permission_ack":
                ApplyPermissionAck(root);
                break;

            case "provider_interrupt":
                Status = NormalizeStatus(Text(root, "status") ?? Status);
                break;

            case "provider_error":
                if (Text(root, "status") is { } errorStatus)
                    Status = NormalizeStatus(errorStatus);
                else if (Text(root, "category") is { } category
                    && category.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                    Status = "cancelled";
                else
                    Status = "failed";
                break;

            case "provider_artifact":
                ApplyFiles(root, "artifact");
                break;
        }
        return true;
    }

    // makes a copy for mutable items so pages recieve a snapshot that cannot be changed in the repository
    public ProviderWorkRoom Clone() => new()
    {
        WorkRoomId = WorkRoomId,
        ParentConversationId = ParentConversationId,
        Provider = Provider,
        ProviderDisplayName = ProviderDisplayName,
        ProviderSessionRef = ProviderSessionRef,
        CurrentInvocationId = CurrentInvocationId,
        ParentToolCallId = ParentToolCallId,
        Capabilities = [.. Capabilities],
        Title = Title,
        Status = Status,
        StateRevision = StateRevision,
        HasAuthoritativeState = HasAuthoritativeState,
        PermissionChangeUnavailable = PermissionChangeUnavailable,
        PermissionAckCounter = PermissionAckCounter,
        PermissionAckAccepted = PermissionAckAccepted,
        PermissionAckOptionId = PermissionAckOptionId,
        PermissionAckError = PermissionAckError,
        AppliedPermissionAckIds = [.. AppliedPermissionAckIds],
        Permission = Permission?.DeepCopy(),
        Messages = [.. Messages],
        Files = [.. Files],
        Approvals = Approvals.Select(a => a with { AllowedOptions = [.. a.AllowedOptions] }).ToList(),
    };

    // checks host capacities before enabling provider controls
    public bool HasCapability(params string[] names)
        => Capabilities.Any(value => names.Contains(value, StringComparer.OrdinalIgnoreCase));

    // checks if provider event can change room authority
    private static bool IsAuthorityEvent(string type) => type is
        "provider_session" or "provider_invocation" or "provider_phase" or "provider_approval" or
        "provider_continuation" or "provider_interrupt" or "provider_error";

    // unwraps activity information before searching for a provider reported file
    private void ApplyFiles(JsonElement root, string wrapper)
    {
        if (Object(root, wrapper) is { } value) root = value;
        AddFiles(root, "path", "filePath", "relativePath");
    }

    // validates added files, updates existing entries, and ensures that the visible file list is bounded
    private void AddFiles(JsonElement root, params string[] extraProperties)
    {
        var paths = ReadSafePaths(root,
            ["files", "affectedFiles", "filePaths", .. extraProperties]);

        var status = Bounded(Text(root, "status"), 40) ?? "updated";

        foreach (var path in paths)
        {
            var index = Files.FindIndex(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
            var file = new ProviderWorkRoomFile(path, status);

            if (index >= 0) Files[index] = file;
            else Files.Add(file);
        }
        Files = Files.TakeLast(100).ToList();
    }

    // validates provider message and inserts it or replaces older message
    private void ApplyMessage(JsonElement root)
    {
        var id = Bounded(Text(root, "messageId") ?? Text(root, "id"), 128);
        var role = Text(root, "role")?.ToLowerInvariant() switch
        {
            "user" => "user",
            "assistant" => "assistant",
            _ => null,
        };

        if (id is null || role is null) return;

        var text = Bounded(Text(root, "text"), 16_000);
        if (text is null) return;

        var revision = Number(root, "revision") ?? 0;
        var existingMessage = Messages.FirstOrDefault(item => item.Id == id);
        if (existingMessage is not null && revision < existingMessage.Revision) return;

        var message = new ProviderWorkRoomMessage(id, role, text, Number(root, "sequence") ?? 0,
            Bounded(Text(root, "lifecycle"), 32) ?? "final", GetClientMessageId(root), "delivered",
            revision, Bounded(Text(root, "author"), 80));

        var index = Messages.FindIndex(item => item.Id == id);
        if (index >= 0) Messages[index] = message;
        else Messages.Add(message);

        NormalizeMessageOrder();
    }

    // orders message by provider order and truncates ones that are beyond the limit
    public void NormalizeMessageOrder()
    {
        Messages = Messages.OrderBy(item => item.Sequence).ThenBy(item => item.Role != "user")
            .TakeLast(MaxMessages).ToList();
    }

    // stores approval request and moves the room into the according next state
    private void ApplyApproval(JsonElement root)
    {
        var id = Text(root, "requestId");
        if (!ValidIdentifier(id, 128)) return;

        var approval = new ProviderWorkRoomApproval(
            id!, Bounded(Text(root, "summary"), 1200) ?? "Approval required",
            Bounded(Text(root, "scope"), 80), Bounded(Text(root, "risk"), 40) ?? "unknown",
            ReadStrings(root, "allowedOptions", 8, 40), Text(root, "resolution"),
            Number(root, "stateRevision") ?? StateRevision,
            Bounded(Text(root, "invocationId"), 128) ?? CurrentInvocationId);

        var index = Approvals.FindIndex(item => item.RequestId == id);
        if (index >= 0)
        {
            var existing = Approvals[index];
            if (approval.Revision < existing.Revision) return;
            Approvals[index] = approval;
        }
        else Approvals.Add(approval);

        Approvals = Approvals.TakeLast(32).ToList();

        if (approval.Resolution is null) Status = "awaiting_approval";
        else if (Status == "awaiting_approval") Status = NormalizeStatus(Text(root, "status") ?? "running");
    }

    // applies the provider's lifecycle update according to the user message that has the matching client id
    private void ApplyContinuation(JsonElement root, string? defaultLifecycle = null)
    {
        var id = GetClientMessageId(root);
        if (!ValidIdentifier(id, 128)) return;

        var status = Bounded(Text(root, "lifecycle") ?? defaultLifecycle, 32);
        if (status is null) return;

        var index = Messages.FindIndex(item => item.ClientMessageId == id || item.Id == id);
        if (index >= 0)
            Messages[index] = Messages[index] with { DeliveryStatus = status };
    }

    // removed duplicates acks from the host and changes the active permission level only when the host confirms an update
    private void ApplyPermissionAck(JsonElement root)
    {
        var ackId = Bounded(Text(root, "requestId"), 128);
        if (ackId is not null && AppliedPermissionAckIds.Contains(ackId, StringComparer.Ordinal)) return;

        if (ackId is not null)
        {
            AppliedPermissionAckIds.Add(ackId);
            AppliedPermissionAckIds = AppliedPermissionAckIds.TakeLast(64).ToList();
        }

        if (PermissionAckCounter < long.MaxValue) PermissionAckCounter++;
        PermissionAckAccepted = ReadAckAccepted(root);
        PermissionAckOptionId = Bounded(Text(root, "optionId"), 128);
        PermissionAckError = null;

        ProviderWorkRoomPermission? updatedPermission = null;

        if (Object(root, "providerPermission") is { } element)
            updatedPermission = ReadPermission(element);

        var currentRevision = Permission?.EffectiveRevision ?? -1;

        if (PermissionAckAccepted != true || updatedPermission is null
            || updatedPermission.EffectiveRevision <= currentRevision
            || (PermissionAckOptionId is { } optionId && updatedPermission.ActiveOptionId != optionId))
        {
            PermissionAckError = Bounded(Text(root, "safeSummary"), 500);
            if (PermissionAckError is null && PermissionAckAccepted == false)
                PermissionAckError = "The host rejected the permission change.";
            else if (PermissionAckError is null)
                PermissionAckError = "The host did not return a newer permission state.";

            PermissionAckAccepted = false;
            PermissionChangeUnavailable = true;
            return;
        }

        Permission = updatedPermission;
        PermissionChangeUnavailable = false;

        var revision = Number(root, "stateRevision");
        StateRevision = Math.Max(StateRevision, Math.Max(updatedPermission.EffectiveRevision, revision ?? 0));
        HasAuthoritativeState = true;
    }

    // returns a property iff it caintains an object the event parser can safely inspect
    private static JsonElement? Object(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object) return null;
        return value;
    }

    // reads whether the host ack was successful
    private static bool? ReadAckAccepted(JsonElement root)
    {
        var accepted = Flag(root, "accepted");
        if (accepted is not null) return accepted;
        return (Text(root, "status") ?? Text(root, "result"))?.ToLowerInvariant() switch
        {
            "accepted" or "acknowledged" or "success" or "applied" => true,
            "rejected" or "failed" or "denied" or "error" => false,
            _ => null,
        };
    }

    // replaces the current permissions dropdown list when the incoming snapshot has a newer version
    private void ApplyPermissionSnapshot(JsonElement element, long? fallbackRevision)
    {
        var updated = ReadPermission(element, fallbackRevision);
        if (updated is null) return;

        if (Permission is { } current && updated.EffectiveRevision <= current.EffectiveRevision) return;

        Permission = updated;
        PermissionChangeUnavailable = updated.UnavailableSummary is not null
            || !updated.Options.Any(option => option.Selectable);
        PermissionAckAccepted = null;
        PermissionAckError = null;
    }

    // parses the host's permission snapshot given the active profile and bounded option list from it
    private static ProviderWorkRoomPermission? ReadPermission(JsonElement root, long? fallbackRevision = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var options = new List<ProviderWorkRoomPermissionOption>();
        if (root.TryGetProperty("options", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in array.EnumerateArray().Take(16))
            {
                if (option.ValueKind != JsonValueKind.Object) continue;
                var id = Text(option, "id");
                if (!ValidIdentifier(id, 128)) continue;
                var selectable = Flag(option, "selectable")
                    ?? (Flag(option, "disabled") is { } disabled && !disabled);
                options.Add(new ProviderWorkRoomPermissionOption(id!, Bounded(Text(option, "nativeProfileId"), 128),
                    Bounded(Text(option, "label"), 80) ?? id!, Bounded(Text(option, "reviewer"), 80),
                    Bounded(Text(option, "description"), 240), Bounded(Text(option, "risk"), 40) ?? "unknown",
                    selectable, Bounded(Text(option, "disabledSummary"), 240)));
            }
        }
        return new ProviderWorkRoomPermission(Bounded(Text(root, "activeOptionId"), 128),
            Number(root, "effectiveRevision") ?? fallbackRevision ?? 0,
            Bounded(Text(root, "unavailableSummary"), 240), Bounded(Text(root, "appliesTo"), 40),
            ReadStrings(root, "effectiveCapabilities", 24, 64), options);
    }

    // gets workspace related paths from event fields up to the file limit
    private static List<string> ReadSafePaths(JsonElement root, params string[] properties)
    {
        var result = new List<string>();
        foreach (var property in properties)
        {
            if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in array.EnumerateArray().Take(100 - result.Count))
            {
                var value = Text(item, "path");

                if (item.ValueKind == JsonValueKind.String) value = item.GetString();
                if (SafeRelativePath(value) is { } path
                    && !result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
            }
            if (result.Count >= 100) break;
        }
        return result;
    }

    // reads strings, removes duplicates, enforces item count and length limits
    private static List<string> ReadStrings(JsonElement root, string name, int maxItems, int maxLength)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in array.EnumerateArray().Take(maxItems))
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var value = Bounded(item.GetString(), maxLength);
            if (value is not null && !result.Contains(value, StringComparer.OrdinalIgnoreCase)) result.Add(value);
        }
        return result;
    }

    // rejects rooted paths and parent directory segments so that reported files stay in the workspace
    private static string? SafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 260) return null;
        var path = value.Replace('\\', '/');
        if (path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains(':')
            || path.Split('/').Any(part => part is ".." or "")) return null;
        return path;
    }

    // accepts non empty identifies within the length limit and rejects control characters
    private static bool ValidIdentifier(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && value.All(c => !char.IsControl(c));

    // trims incoming text
    private static string? Bounded(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text[..Math.Min(text.Length, maxLength)];
    }

    // returns a property iff the JSON value is a string
    private static string? Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String) return null;
        return value.GetString();
    }

    // returns a JSON int iiff it fits in a 64 bit value
    private static long? Number(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) return null;
        return number;
    }

    // returns true/false for JSON booleans
    private static bool? Flag(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return null;
    }

    // ensures the status matches what the ui expects
    public static string NormalizeStatus(string? value) => value switch
    {
        "starting" or "running" or "awaiting_approval" or "completed" or "failed" or "unavailable"
            or "cancelled" or "thinking" or "working" or "awaiting_input" or "stopping" => value,
        _ => "unavailable",
    };

    // converts provider status into a header for the workrooms page
    public static string StatusLabel(string? status) => status switch
    {
        "starting" => "Starting",
        "running" or "working" => "Working",
        "thinking" => "Thinking",
        "awaiting_approval" => "Approval needed",
        "awaiting_input" => "Waiting for input",
        "stopping" => "Stopping",
        "completed" => "Completed",
        "failed" => "Failed",
        "cancelled" => "Stopped",
        _ => "Unavailable",
    };

    // converts workroom page header into provider status
    public static string? StatusFromLabel(string label) => label switch
    {
        "Starting" => "starting",
        "Running" or "Working" => "running",
        "Thinking" => "thinking",
        "Approval needed" => "awaiting_approval",
        "Waiting for input" => "awaiting_input",
        "Stopping" => "stopping",
        "Completed" => "completed",
        "Failed" => "failed",
        "Stopped" => "cancelled",
        _ => null,
    };

    // converts allowed provider ids to product names for the workroom, otherwise falls back to generic label
    public static string ProviderLabel(string provider) => provider switch
    {
        "codex" => "Codex",
        "claude" or "claude_code" => "Claude Code",
        _ => Bounded(provider, 48) ?? "Coding agent",
    };
}

// record for one provider or user message
public sealed record ProviderWorkRoomMessage(string Id, string Role, string Text, long Sequence, string Lifecycle,
string? ClientMessageId, string DeliveryStatus, long Revision = 0, string? Author = null)
{
    [JsonIgnore]
    public bool IsAssistant => Role == "assistant";

    [JsonIgnore]
    public bool IsUser => Role == "user";

    [JsonIgnore]
    public string DeliveryLabel => DeliveryStatus switch
    {
        "queued" => "Sending",
        "acknowledged" => "Sent to host",
        "unconfirmed" => "Not confirmed, confirm status before retrying",
        "failed" => "Unable to be sent",
        _ => "",
    };
}

// record for a workspace file path reported by the provider
public sealed record ProviderWorkRoomFile(string Path, string Status);

// record for one host advertised provider permission level and its risk rating
public sealed record ProviderWorkRoomPermissionOption(
    string Id, string? NativeProfileId, string Label, string? Reviewer,
    string? Description, string Risk, bool Selectable, string? DisabledSummary);

// record for host's active provider profile, revision, capabilities and selectable options
public sealed record ProviderWorkRoomPermission(string? ActiveOptionId, long EffectiveRevision,
    string? UnavailableSummary, string? AppliesTo, List<string> EffectiveCapabilities,
    List<ProviderWorkRoomPermissionOption> Options)
{
    public ProviderWorkRoomPermission DeepCopy() => this with
    {
        EffectiveCapabilities = EffectiveCapabilities?.ToList() ?? [],
        Options = Options?.ToList() ?? [],
    };
}

// record for a provider approval request with the allowed responses and current resolution
public sealed record ProviderWorkRoomApproval(string RequestId, string Summary, string? Scope, string Risk,
    List<string> AllowedOptions, string? Resolution, long Revision, string? InvocationId)
{
    public bool AllowsApprove => AllowedOptions.Any(option =>
        option.Equals("approve", StringComparison.OrdinalIgnoreCase)
        || option.Equals("accept", StringComparison.OrdinalIgnoreCase)
        || option.Equals("allow", StringComparison.OrdinalIgnoreCase));
    public bool AllowsReject => AllowedOptions.Any(option =>
        option.Equals("reject", StringComparison.OrdinalIgnoreCase)
        || option.Equals("deny", StringComparison.OrdinalIgnoreCase));
    public bool IsPending => Resolution is null;
}
