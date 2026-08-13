namespace ConnectOnion.WinUIClient.Models;

/// <summary>One readable section of a Markdown execution plan.</summary>
public sealed record PlanReviewSection(string Heading, string Markdown);

/// <summary>
/// Splits a plan at Markdown headings for section-level review. This intentionally is not a full
/// Markdown parser: it preserves each section body verbatim for the existing renderer and never
/// rewrites the plan sent by the agent.
/// </summary>
public static class PlanReviewSections
{
    public static IReadOnlyList<PlanReviewSection> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];

        var result = new List<PlanReviewSection>();
        var heading = "Overview";
        var body = new List<string>();
        var sawHeading = false;

        void Flush()
        {
            var text = string.Join(Environment.NewLine, body).Trim();
            if (text.Length > 0 || sawHeading)
                result.Add(new PlanReviewSection(heading, text));
            body.Clear();
        }

        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimStart();
            var hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            var isHeading = hashes is >= 1 and <= 6
                && hashes < trimmed.Length
                && char.IsWhiteSpace(trimmed[hashes]);
            if (!isHeading)
            {
                body.Add(line);
                continue;
            }

            Flush();
            heading = trimmed[(hashes + 1)..].Trim();
            if (heading.Length == 0) heading = "Untitled section";
            sawHeading = true;
        }
        Flush();

        return result.Count > 0
            ? result
            : [new PlanReviewSection("Plan", markdown.Trim())];
    }
}
