using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

/// <summary>
/// The approval card's risk line. It used to be a constant — every command produced "Risk: This
/// command may delete or modify server data" — which is why these tests care most about the two
/// directions of being wrong, not about breadth of coverage.
/// </summary>
public class CommandRiskAssessorTests
{
    /// <summary>The case that prompted the change: <c>-n</c> is <c>--dry-run</c> for
    /// <c>git clean</c>. It prints what it would remove and removes nothing, and the card called it
    /// destructive.</summary>
    [Theory]
    [InlineData("git clean -d -n")]
    [InlineData("git clean --dry-run -d")]
    [InlineData("cd /home/ubuntu/project && git clean -d -n")]
    [InlineData("rsync --dry-run -av src/ dst/")]
    public void DryRunNeutralisesAnOtherwiseDestructiveCommand(string command)
        => Assert.Equal(CommandRisk.ReadOnly, CommandRiskAssessor.Assess(command));

    /// <summary>The same flag on a command with no dry-run semantics means something else entirely
    /// (<c>echo -n</c> suppresses a newline, <c>sort -n</c> sorts numerically). It must never read
    /// as safety, and it must never downgrade a genuinely destructive command.</summary>
    [Theory]
    [InlineData("rm -rf /var/data", CommandRisk.Destructive)]
    [InlineData("git clean -d -f", CommandRisk.Destructive)]
    [InlineData("git push --force origin main", CommandRisk.Destructive)]
    [InlineData("sudo systemctl restart nginx", CommandRisk.Destructive)]
    [InlineData("dd if=/dev/zero of=/dev/sda", CommandRisk.Destructive)]
    [InlineData("chmod -R 777 /etc", CommandRisk.Destructive)]
    [InlineData("kubectl delete pod api-0", CommandRisk.Destructive)]
    [InlineData("npm publish", CommandRisk.Destructive)]
    public void DestructiveCommandsAreReported(string command, CommandRisk expected)
        => Assert.Equal(expected, CommandRiskAssessor.Assess(command));

    [Theory]
    [InlineData("ls -la")]
    [InlineData("cat /etc/hosts")]
    [InlineData("git status")]
    [InlineData("git log --oneline -20")]
    [InlineData("cd /srv && ls")]
    [InlineData("grep -rn TODO src/")]
    [InlineData("/usr/bin/whoami")]
    [InlineData("PAGER=cat git diff")]
    public void ReadOnlyCommandsAreRecognised(string command)
        => Assert.Equal(CommandRisk.ReadOnly, CommandRiskAssessor.Assess(command));

    /// <summary>The whole command is only as safe as its least understood segment. One
    /// unrecognised piece pins the result at Unknown even when everything around it is on the
    /// allow-list; one destructive piece wins outright.</summary>
    [Theory]
    [InlineData("ls && ./deploy.sh", CommandRisk.Unknown)]
    [InlineData("cat file.txt | some-unknown-tool", CommandRisk.Unknown)]
    [InlineData("ls; rm -rf build", CommandRisk.Destructive)]
    [InlineData("./configure && make && sudo make install", CommandRisk.Destructive)]
    public void TheWorstSegmentDecides(string command, CommandRisk expected)
        => Assert.Equal(expected, CommandRiskAssessor.Assess(command));

    /// <summary>A redirect writes a file whatever the verb in front of it is, so an allow-listed
    /// reader plus <c>&gt;</c> is not a read. <c>2&gt;&amp;1</c> only re-points a stream and must
    /// not trip it.</summary>
    [Theory]
    [InlineData("cat secrets.env > /tmp/leak", CommandRisk.Destructive)]
    [InlineData("echo broken >> /etc/hosts", CommandRisk.Destructive)]
    [InlineData("ls -la 2>&1", CommandRisk.ReadOnly)]
    public void RedirectionCountsAsAWrite(string command, CommandRisk expected)
        => Assert.Equal(expected, CommandRiskAssessor.Assess(command));

    /// <summary>Unknown is the default, and it is a caution rather than an all-clear. Calling a
    /// dangerous command safe is a real harm; calling a safe one unrecognised is an annoyance —
    /// so there is no inference in the safe direction anywhere in the assessor.</summary>
    [Theory]
    [InlineData("./scripts/migrate.sh")]
    [InlineData("python manage.py migrate")]
    [InlineData("some-vendor-cli --apply")]
    [InlineData("git bisect start")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingUnrecognisedStaysUnknown(string? command)
        => Assert.Equal(CommandRisk.Unknown, CommandRiskAssessor.Assess(command));

    /// <summary>A path or a quote in front of the executable must not hide it.</summary>
    [Theory]
    [InlineData("/bin/rm -rf /tmp/x")]
    [InlineData("./rm something")]
    [InlineData("\"/usr/bin/rm\" -f a")]
    public void PathsAndQuotesDoNotHideTheExecutable(string command)
        => Assert.Equal(CommandRisk.Destructive, CommandRiskAssessor.Assess(command));
}
