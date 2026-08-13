using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>How much damage a command the agent wants to run could do.</summary>
public enum CommandRisk
{
    /// <summary>Every segment was recognised as read-only, or neutralised by a dry-run flag.
    /// Nothing on disk or on the network changes.</summary>
    ReadOnly,

    /// <summary>Nothing matched. <b>This is the default and it is a caution, not an all-clear</b> —
    /// see the class remarks.</summary>
    Unknown,

    /// <summary>A segment matched something that deletes, overwrites, escalates or publishes.</summary>
    Destructive,
}

/// <summary>
/// Derives an approval card's risk line from the command actually being approved.
///
/// <para><b>Why this exists.</b> The risk text used to be a constant: every command, whatever it
/// was, produced "Risk: This command may delete or modify server data." A warning that fires
/// identically on a <c>ls</c> and on an <c>rm -rf /</c> carries no information, and a user who sees
/// it on every approval learns to click past it — which spends the card's entire safety value
/// before the one request that needed it arrives. The screenshot case was
/// <c>git clean -d -n</c>: <c>-n</c> is <c>--dry-run</c>, it deletes nothing, and the card called it
/// destructive anyway.</para>
///
/// <para><b>The asymmetry that governs every rule here.</b> Calling a dangerous command safe is a
/// real harm; calling a safe command unknown is a small annoyance. So <see cref="CommandRisk"/>
/// only reaches <see cref="CommandRisk.ReadOnly"/> when <i>every</i> segment is on an explicit
/// allow-list, and any unrecognised token anywhere pins the whole command at
/// <see cref="CommandRisk.Unknown"/>. There is no inference in the safe direction. Destructive, by
/// contrast, needs only one match.</para>
///
/// <para><b>This is a display aid, not a sandbox.</b> It informs the wording of a warning shown to
/// a human who is about to make the decision themselves. It must never be used to skip an approval,
/// auto-answer one, or gate execution — the agent host is the enforcement boundary, and a regex over
/// a shell string is trivially defeated by an alias, a variable, or a script file.</para>
/// </summary>
public static class CommandRiskAssessor
{
    /// <summary>Splits on the shell operators that start a new command. Deliberately crude: it
    /// over-splits rather than under-splits, because a segment we fail to isolate is a segment we
    /// fail to inspect.</summary>
    private static readonly Regex SegmentSplitter = new(
        @"(?:&&|\|\||[;|&\n])", RegexOptions.Compiled);

    /// <summary>Stream duplication (<c>2&gt;&amp;1</c>, <c>&gt;&amp;2</c>) — points one stream at
    /// another, writes no file. Removed <b>before</b> splitting, because the splitter treats a bare
    /// <c>&amp;</c> as a separator and would otherwise tear <c>2&gt;&amp;1</c> into a segment
    /// ending in <c>2&gt;</c>, which then looks exactly like a redirect into a file. That made
    /// <c>ls -la 2&gt;&amp;1</c> read as destructive.</summary>
    private static readonly Regex StreamDuplication = new(@"\d*>&\d+", RegexOptions.Compiled);

    /// <summary>Flags that turn a mutating command into a report. <c>-n</c> is included because it
    /// is <c>--dry-run</c> for <c>git clean</c>, <c>rsync</c>, <c>make</c> and <c>patch</c>; it is
    /// <i>not</i> universal, so it only ever downgrades a command already recognised as
    /// dry-runnable.</summary>
    private static readonly Regex DryRunFlag = new(
        @"(?:^|\s)(?:--dry-run|--just-print|--no-act|-n)(?:\s|$|=)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Commands whose destructive form has a documented dry-run flag. Only these can be
    /// downgraded by <see cref="DryRunFlag"/>; a bare <c>-n</c> on anything else means something
    /// different (<c>echo -n</c>, <c>sort -n</c>, <c>grep -n</c>) and must not read as safety.</summary>
    private static readonly HashSet<string> DryRunnable = new(StringComparer.OrdinalIgnoreCase)
    {
        "clean", "rsync", "make", "patch", "rm", "apt", "apt-get", "pip", "npm", "git",
    };

    /// <summary>Read-only utilities. An allow-list, never a deny-list — anything absent is
    /// <see cref="CommandRisk.Unknown"/>, which is the conservative answer.</summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "ls", "ll", "dir", "pwd", "cat", "bat", "head", "tail", "less", "more", "wc",
        "grep", "egrep", "rgrep", "rg", "ag", "ack", "find", "fd", "locate", "which", "whereis",
        "file", "stat", "du", "df", "tree", "echo", "printf", "date", "whoami", "id", "uname",
        "hostname", "uptime", "env", "printenv", "ps", "top", "free", "diff", "cmp", "md5sum",
        "sha256sum", "sort", "uniq", "cut", "awk", "sed", "jq", "column", "nl", "basename",
        "dirname", "realpath", "readlink", "true", "false", "test", "type", "man", "help",
    };

    /// <summary>Subcommands that make an otherwise-ambiguous VCS/package tool read-only.</summary>
    private static readonly Dictionary<string, HashSet<string>> ReadOnlySubcommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["git"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "status", "log", "diff", "show", "blame", "branch", "remote", "config",
                "describe", "rev-parse", "ls-files", "ls-remote", "shortlog", "reflog", "tag",
            },
            ["docker"] = new(StringComparer.OrdinalIgnoreCase) { "ps", "images", "logs", "inspect", "version" },
            ["kubectl"] = new(StringComparer.OrdinalIgnoreCase) { "get", "describe", "logs", "version" },
            ["npm"] = new(StringComparer.OrdinalIgnoreCase) { "list", "ls", "view", "outdated", "audit" },
            ["pip"] = new(StringComparer.OrdinalIgnoreCase) { "list", "show", "freeze" },
            ["dotnet"] = new(StringComparer.OrdinalIgnoreCase) { "--version", "--info", "--list-sdks" },
        };

    /// <summary>Executables that delete, overwrite, escalate, or reach the network to publish.
    /// A match anywhere in the command is enough.</summary>
    private static readonly HashSet<string> DestructiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "unlink", "shred", "dd", "mkfs", "fdisk", "parted", "format",
        "chmod", "chown", "chgrp", "mv", "truncate",
        // These write to a destination. Listing them here is also what lets --dry-run downgrade
        // them: the downgrade only ever applies to something already recognised as destructive.
        "cp", "rsync", "ln", "tee", "patch", "install",
        "kill", "killall", "pkill", "reboot", "shutdown", "halt", "poweroff", "systemctl",
        "sudo", "su", "doas",
        "apt", "apt-get", "yum", "dnf", "pacman", "brew", "snap",
        "crontab", "iptables", "ufw", "mount", "umount", "swapoff",
    };

    /// <summary>Destructive <c>tool subcommand</c> pairs — the tool alone says nothing.</summary>
    private static readonly Dictionary<string, HashSet<string>> DestructiveSubcommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["git"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "clean", "reset", "checkout", "restore", "push", "rebase", "filter-branch", "gc", "prune",
            },
            ["docker"] = new(StringComparer.OrdinalIgnoreCase) { "rm", "rmi", "prune", "kill", "stop", "system" },
            ["kubectl"] = new(StringComparer.OrdinalIgnoreCase) { "delete", "apply", "drain", "cordon", "scale" },
            ["npm"] = new(StringComparer.OrdinalIgnoreCase) { "publish", "install", "uninstall", "update" },
            ["pip"] = new(StringComparer.OrdinalIgnoreCase) { "install", "uninstall" },
        };

    /// <summary>Output redirection: <c>&gt;</c> and <c>&gt;&gt;</c> overwrite or append to a file
    /// whatever the command in front of them is, so <c>cat x &gt; y</c> is not a read.
    /// <c>2&gt;&amp;1</c> and process substitution are excluded — they redirect between streams.</summary>
    private static readonly Regex WritingRedirect = new(@">>?(?!&)", RegexOptions.Compiled);

    /// <summary>The worst thing any segment of <paramref name="command"/> could do.</summary>
    public static CommandRisk Assess(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return CommandRisk.Unknown;

        var risk = CommandRisk.ReadOnly;
        foreach (var segment in SegmentSplitter.Split(StreamDuplication.Replace(command, " ")))
        {
            var segmentRisk = AssessSegment(segment);
            // Destructive wins outright; otherwise the whole command is only as safe as its least
            // understood part.
            if (segmentRisk == CommandRisk.Destructive) return CommandRisk.Destructive;
            if (segmentRisk == CommandRisk.Unknown) risk = CommandRisk.Unknown;
        }

        return risk;
    }

    private static CommandRisk AssessSegment(string segment)
    {
        var trimmed = segment.Trim();
        // An empty piece is a splitter artifact (`a && b` yields no empty parts, but `a | | b` and
        // trailing operators do). It carries no risk of its own.
        if (trimmed.Length == 0) return CommandRisk.ReadOnly;

        // A redirect makes the segment a write regardless of the verb in front of it.
        if (WritingRedirect.IsMatch(trimmed)) return CommandRisk.Destructive;

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return CommandRisk.ReadOnly;

        // Strip an env-var prefix (`FOO=bar cmd`) and a path (`/usr/bin/rm` is still rm).
        var index = 0;
        while (index < tokens.Length && tokens[index].Contains('=', StringComparison.Ordinal)
               && !tokens[index].StartsWith('-'))
        {
            index++;
        }
        if (index >= tokens.Length) return CommandRisk.Unknown;

        var name = Executable(tokens[index]);
        var subcommand = tokens.Skip(index + 1).FirstOrDefault(token => !token.StartsWith('-'));

        var destructive = DestructiveCommands.Contains(name)
            || (subcommand is not null
                && DestructiveSubcommands.TryGetValue(name, out var bad)
                && bad.Contains(subcommand));

        if (destructive)
        {
            // The one downgrade in the whole assessor, and it is narrow on purpose: the command has
            // to be one whose destructive form documents a dry-run flag, and the flag has to be
            // present. `git clean -d -n` reports what it would delete and deletes nothing.
            var dryRunnable = DryRunnable.Contains(name)
                || (subcommand is not null && DryRunnable.Contains(subcommand));
            return dryRunnable && DryRunFlag.IsMatch(trimmed)
                ? CommandRisk.ReadOnly
                : CommandRisk.Destructive;
        }

        if (ReadOnlySubcommands.TryGetValue(name, out var safeSubcommands))
        {
            return subcommand is not null && safeSubcommands.Contains(subcommand)
                ? CommandRisk.ReadOnly
                : CommandRisk.Unknown;
        }

        return ReadOnlyCommands.Contains(name) ? CommandRisk.ReadOnly : CommandRisk.Unknown;
    }

    /// <summary>The executable name without its directory, so <c>/bin/rm</c> and <c>./rm</c> are
    /// both <c>rm</c>. Quotes are stripped because a quoted path is still a path.</summary>
    private static string Executable(string token)
    {
        var cleaned = token.Trim('"', '\'', '(', ')');
        var slash = cleaned.LastIndexOfAny(['/', '\\']);
        return slash >= 0 && slash + 1 < cleaned.Length ? cleaned[(slash + 1)..] : cleaned;
    }
}
