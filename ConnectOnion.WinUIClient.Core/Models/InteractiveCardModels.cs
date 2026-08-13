using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.Models;

public enum InteractiveCardPhase
{
    Pending,
    Submitting,
    Submitted,
    Completed,
    Cancelled,
    Rejected,
    Expired,
    ConnectionLost,
    Error,
}

public enum InteractiveVisualTone
{
    Neutral,
    Warning,
    Success,
    Danger,
}

public enum PlanReviewAction
{
    Approve,
    RequestChanges,
    Reject,
}

public enum DiffLineKind
{
    Context,
    Addition,
    Deletion,
    Hunk,
    FileHeader,
}

/// <summary>The durable outcome of a proposed file change. This is deliberately separate from
/// the card's expansion flag: state answers what happened to the file, expansion only answers
/// what the user currently wants to see.</summary>
public enum DiffChangeState
{
    Preview,
    Pending,
    Applying,
    Applied,
    Rejected,
    Failed,
    PartiallyApplied,
    Disconnected,
    Unconfirmed,
}

public sealed partial class DiffLineModel : Common.ObservableObject
{
    public int? OldLineNumber { get; set; }
    public int? NewLineNumber { get; set; }
    /// <summary>A unified-diff row has one visual gutter. Deleted rows use the source line,
    /// while additions and context use the resulting-file line. The complete old/new pair stays
    /// available to accessibility through <see cref="AccessibilityName"/>.</summary>
    public int? DisplayLineNumber => Kind == DiffLineKind.Deletion
        ? OldLineNumber
        : NewLineNumber ?? OldLineNumber;
    public string Marker { get; set; } = " ";
    public string Content { get; set; } = "";
    public string RawText { get; set; } = "";
    public DiffLineKind Kind { get; set; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsWrapped { get; set; }
    public string AccessibilityName =>
        $"{Kind}. Old line {OldLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, new line {NewLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}. {Content}";
}

public sealed partial class DiffFileModel : Common.ObservableObject
{
    public string Path { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string DisplayPath => DiffPathFormatter.MiddleEllipsis(FullPath);
    public string FileName => DiffPathFormatter.FileName(FullPath);
    public int Additions { get; set; }
    public int Deletions { get; set; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsExpanded { get; set; }
    public bool ShowHeader { get; set; }
    public ObservableCollection<DiffLineModel> Lines { get; } = new();
    public string ChangeSummary => $"+{Additions}  −{Deletions}";
}

internal static class DiffPathFormatter
{
    public static string MiddleEllipsis(string path, int maximum = 64)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maximum) return path;
        var tailLength = Math.Min(36, maximum / 2 + 4);
        var headLength = maximum - tailLength - 1;
        return $"{path[..headLength]}…{path[^tailLength..]}";
    }

    public static string FileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Proposed changes";
        var index = path.LastIndexOfAny(['/', '\\']);
        return index >= 0 && index < path.Length - 1 ? path[(index + 1)..] : path;
    }

    public static string ContextPath(string path, int segments = 4)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= segments) return path;
        return $"…/{string.Join('/', parts[^segments..])}";
    }
}

/// <summary>Presentation-only parser for the real diff_preview payload. It never creates a
/// response and treats ordinary/Markdown text as context unless unified-diff markers prove the
/// body is a diff.</summary>
public sealed class DiffPreviewModel
{
    public const int MaxPreviewLines = 240;

    private static readonly Regex Hunk = new(
        "^@@ -(?<old>\\d+)(?:,(?<oldCount>\\d+))? \\+(?<new>\\d+)(?:,(?<newCount>\\d+))? @@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ObservableCollection<DiffFileModel> Files { get; } = new();
    public string RawText { get; private set; } = "";
    public bool IsUnifiedDiff { get; private set; }
    public int OmittedLineCount { get; private set; }
    public bool IsPreviewTruncated => OmittedLineCount > 0;
    public string TruncationLabel => Common.CoreStrings.Format(
        "DiffPreviewTruncated", "Preview shortened; {0} additional lines are available when copied.", OmittedLineCount);
    public int Additions => Files.Sum(file => file.Additions);
    public int Deletions => Files.Sum(file => file.Deletions);
    public string FileSummary => Files.Count == 1 ? Files[0].FileName : $"{Files.Count} files changed";
    public string ChangeSummary => $"+{Additions}  −{Deletions}";
    public bool IsWrapped { get; set; }

    public static DiffPreviewModel Parse(string path, string? preview, bool isNewFile = false)
    {
        var model = new DiffPreviewModel { RawText = preview ?? "" };
        var lines = Normalize(preview);
        model.IsUnifiedDiff = isNewFile
            || lines.Any(line => line.StartsWith("@@", StringComparison.Ordinal))
            || (lines.Any(line => line.StartsWith("--- ", StringComparison.Ordinal))
                && lines.Any(line => line.StartsWith("+++ ", StringComparison.Ordinal)));

        var current = NewFile(path);
        model.Files.Add(current);
        var currentHasContent = false;
        var oldLine = 0;
        var newLine = isNewFile ? 1 : 0;

        foreach (var raw in lines)
        {
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var nextPath = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
                nextPath = TrimDiffPrefix(nextPath);
                if (!currentHasContent && current.Additions == 0 && current.Deletions == 0)
                {
                    current.Path = nextPath;
                    current.FullPath = nextPath;
                }
                else
                {
                    current = NewFile(nextPath);
                    model.Files.Add(current);
                    currentHasContent = false;
                }
                currentHasContent = true;
                model.AddPreviewLine(current, Line(raw, DiffLineKind.FileHeader));
                continue;
            }

            var hunk = model.IsUnifiedDiff ? Hunk.Match(raw) : Match.Empty;
            if (hunk.Success)
            {
                oldLine = int.Parse(hunk.Groups["old"].Value, System.Globalization.CultureInfo.InvariantCulture);
                newLine = int.Parse(hunk.Groups["new"].Value, System.Globalization.CultureInfo.InvariantCulture);
                currentHasContent = true;
                model.AddPreviewLine(current, Line(raw, DiffLineKind.Hunk));
                continue;
            }

            if (!model.IsUnifiedDiff || raw.StartsWith("--- ", StringComparison.Ordinal)
                || raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentHasContent = true;
                model.AddPreviewLine(current, Line(raw,
                    model.IsUnifiedDiff && (raw.StartsWith("--- ", StringComparison.Ordinal)
                        || raw.StartsWith("+++ ", StringComparison.Ordinal))
                        ? DiffLineKind.FileHeader : DiffLineKind.Context));
                continue;
            }

            if (raw.StartsWith('+'))
            {
                currentHasContent = true;
                model.AddPreviewLine(current, Line(raw, DiffLineKind.Addition, null, newLine++));
                current.Additions++;
            }
            else if (raw.StartsWith('-'))
            {
                currentHasContent = true;
                model.AddPreviewLine(current, Line(raw, DiffLineKind.Deletion, oldLine++, null));
                current.Deletions++;
            }
            else
            {
                currentHasContent = true;
                model.AddPreviewLine(current, Line(raw, DiffLineKind.Context, oldLine++, newLine++));
            }
        }

        if (model.Files.Count > 0) model.Files[0].IsExpanded = true;
        var showHeaders = model.Files.Count > 1;
        foreach (var file in model.Files) file.ShowHeader = showHeaders;
        return model;
    }

    private int _previewLineCount;

    private void AddPreviewLine(DiffFileModel file, DiffLineModel line)
    {
        if (_previewLineCount < MaxPreviewLines)
        {
            file.Lines.Add(line);
            _previewLineCount++;
        }
        else
        {
            OmittedLineCount++;
        }
    }

    private static List<string> Normalize(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').ToList();

        // Some DiffWriter versions concatenate the final removed and added records when both
        // source files omit their trailing newline. The hunk header still declares one record
        // on each side, but the payload ends in e.g. "-old+new". Repair only that unambiguous
        // one-marker deficit for presentation; RawText deliberately stays byte-for-byte what
        // arrived so Copy diff never invents wire content.
        for (var hunkIndex = 0; hunkIndex < lines.Count; hunkIndex++)
        {
            var hunk = Hunk.Match(lines[hunkIndex]);
            if (!hunk.Success) continue;

            var end = hunkIndex + 1;
            while (end < lines.Count
                   && !Hunk.IsMatch(lines[end])
                   && !lines[end].StartsWith("diff --git ", StringComparison.Ordinal))
                end++;

            RepairJoinedReplacement(lines, hunkIndex + 1, end, hunk);
            hunkIndex = end - 1;
        }

        return lines;
    }

    private static void RepairJoinedReplacement(
        List<string> lines, int start, int end, Match hunk)
    {
        var expectedOld = HunkCount(hunk, "oldCount");
        var expectedNew = HunkCount(hunk, "newCount");
        var observedOld = 0;
        var observedNew = 0;

        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            if (line.Length == 0) return;
            switch (line[0])
            {
                case '-': observedOld++; break;
                case '+': observedNew++; break;
                case ' ': observedOld++; observedNew++; break;
                case '\\': break; // "No newline at end of file" marker.
                default: return; // A truncated/non-diff body is not safe to reconstruct.
            }
        }

        if (observedOld != expectedOld || observedNew + 1 != expectedNew) return;

        var candidateIndex = -1;
        var markerIndex = -1;
        for (var index = start; index < end; index++)
        {
            if (!lines[index].StartsWith('-')) continue;
            var firstPlus = lines[index].IndexOf('+', 1);
            if (firstPlus < 0 || lines[index].IndexOf('+', firstPlus + 1) >= 0) continue;
            if (candidateIndex >= 0) return;
            candidateIndex = index;
            markerIndex = firstPlus;
        }

        if (candidateIndex < 0) return;
        var joined = lines[candidateIndex];
        lines[candidateIndex] = joined[..markerIndex];
        lines.Insert(candidateIndex + 1, joined[markerIndex..]);
    }

    private static int HunkCount(Match hunk, string groupName)
        => hunk.Groups[groupName].Success
            ? int.Parse(hunk.Groups[groupName].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 1;

    private static DiffFileModel NewFile(string path)
    {
        var clean = TrimDiffPrefix(string.IsNullOrWhiteSpace(path)
            ? Common.CoreStrings.Get("DiffUnnamedFile", "Proposed changes")
            : path);
        return new DiffFileModel { Path = clean, FullPath = clean };
    }

    private static string TrimDiffPrefix(string path)
        => path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..] : path;

    private static DiffLineModel Line(
        string raw, DiffLineKind kind, int? oldLine = null, int? newLine = null)
    {
        var hasMarker = kind is DiffLineKind.Addition or DiffLineKind.Deletion;
        return new DiffLineModel
        {
            RawText = raw,
            Marker = hasMarker && raw.Length > 0 ? raw[..1] : " ",
            Content = hasMarker && raw.Length > 0 ? raw[1..] : raw,
            Kind = kind,
            OldLineNumber = oldLine,
            NewLineNumber = newLine,
        };
    }
}
