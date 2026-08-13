using System.Collections.Generic;
using System.Linq;

namespace ConnectOnion.Protocol;

/// <summary>
/// Builds the INPUT wire frame as a plain <see cref="Dictionary{TKey, TValue}"/>,
/// matching <see cref="AgentConnectionService"/>'s existing convention of building
/// every outgoing message as an untyped dictionary, serialized by
/// <see cref="WireJson"/> (no source-gen context, no attribute-based DTOs).
///
/// Pulled out of <see cref="AgentConnectionService"/> so the exact shape — text-only,
/// image-only, file-only, and mixed — is independently unit-testable without a live
/// socket. Schema is the one confirmed in
/// <c>connectonion/docs/network/websocket-protocol.md</c> §INPUT and mirrored by the
/// TypeScript SDK's <c>remote-agent.ts:132-135</c>:
/// <code>
/// { "type": "INPUT", "input_id": "...", "prompt": "...",
///   "images": ["data:image/png;base64,..."],
///   "files": [{ "name": "doc.pdf", "data": "data:application/pdf;base64,..." }] }
/// </code>
/// <c>images</c>/<c>files</c> are omitted entirely when empty rather than sent as
/// empty arrays, matching the TS SDK (<c>if (options?.images?.length) msg.images = ...</c>)
/// so a text-only send is byte-for-byte identical to before this feature existed.
/// </summary>
public static class InputMessageBuilder
{
    public static Dictionary<string, object?> BuildInput(
        string prompt,
        string inputId,
        string? toAddress,
        IReadOnlyList<string>? images = null,
        IReadOnlyList<OutgoingFileAttachment>? files = null)
    {
        var msg = new Dictionary<string, object?>
        {
            ["type"] = "INPUT",
            ["input_id"] = inputId,
            ["prompt"] = prompt,
        };

        if (images is { Count: > 0 })
        {
            msg["images"] = images;
        }

        if (files is { Count: > 0 })
        {
            msg["files"] = files
                .Select(f => new Dictionary<string, object?> { ["name"] = f.Name, ["data"] = f.DataUrl })
                .ToList();
        }

        if (toAddress is not null)
        {
            msg["to"] = toAddress;
        }

        return msg;
    }
}
