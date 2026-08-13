using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Data;

public sealed class AppStorageTests
{
    [Fact]
    public void SelectRootDir_Packaged_UsesLocalStateFolder()
    {
        var localState = Path.Combine(Path.GetTempPath(), "package", "LocalState");

        Assert.Equal(
            Path.Combine(localState, "ConnectOnion"),
            AppStorage.SelectRootDir(null, localState, null));
    }

    [Fact]
    public void SelectRootDir_Unpackaged_UsesRoamingAppData()
    {
        var roaming = Path.Combine(Path.GetTempPath(), "Roaming");

        Assert.Equal(
            Path.Combine(roaming, "ConnectOnion"),
            AppStorage.SelectRootDir(null, null, roaming));
    }

    [Fact]
    public void SelectRootDir_OverrideWinsAcrossDeploymentModels()
    {
        var configured = Path.Combine(Path.GetTempPath(), "isolated");

        Assert.Equal(
            Path.GetFullPath(configured),
            AppStorage.SelectRootDir(configured, "ignored-package", "ignored-roaming"));
    }

    [Fact]
    public void CreateAgentIconRelativePath_UsesForwardSlashesUnderTheAvatarsFolder()
    {
        // The separator is part of the stored value: it goes into agents.icon_path and has to read
        // back the same way regardless of which platform wrote it.
        Assert.Equal("avatars/agent-1.png", AppStorage.CreateAgentIconRelativePath("agent-1.png"));
    }

    [Theory]
    [InlineData("../connectonion.db")]
    [InlineData("avatars/../../secrets.png")]
    [InlineData("avatars/nested/../../../outside.png")]
    public void GetAgentIconAbsolutePath_PathEscapingTheAvatarsFolder_IsRejected(string relativePath)
    {
        // icon_path is a database column. A copied or hand-edited database must not be able to
        // point the avatar loader — or the icon delete paths — at a file elsewhere on disk.
        Assert.Throws<InvalidOperationException>(() => AppStorage.GetAgentIconAbsolutePath(relativePath));
    }

    [Fact]
    public void GetAgentIconAbsolutePath_PathInsideTheAvatarsFolder_Resolves()
    {
        var absolutePath = AppStorage.GetAgentIconAbsolutePath("avatars/agent-1.png");

        Assert.Equal(Path.Combine(AppStorage.AgentIconsDir, "agent-1.png"), absolutePath);
    }

    [Fact]
    public void TryGetAgentIconAbsolutePath_UnusableValue_ReportsFailureInsteadOfThrowing()
    {
        // The display path calls this for every row, and an agent whose icon no longer resolves
        // has to fall back to its initial rather than take the sidebar down.
        Assert.False(AppStorage.TryGetAgentIconAbsolutePath(null, out _));
        Assert.False(AppStorage.TryGetAgentIconAbsolutePath("   ", out _));
        Assert.False(AppStorage.TryGetAgentIconAbsolutePath("../escape.png", out _));
        Assert.True(AppStorage.TryGetAgentIconAbsolutePath("avatars/agent-1.png", out var resolved));
        Assert.NotNull(resolved);
    }

    [Theory]
    [InlineData("nested/agent.png")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("bad|name.png")]
    public void GetPermanentAgentIconAbsolutePath_NonLeafFileName_IsRejected(string fileName)
    {
        Assert.Throws<ArgumentException>(() => AppStorage.GetPermanentAgentIconAbsolutePath(fileName));
    }

    [Fact]
    public void IsPathInsideDirectory_SiblingWithTheSamePrefix_IsOutside()
    {
        // The trailing separator is the whole point: "…\avatars-backup" starts with "…\avatars".
        var root = Path.Combine(Path.GetTempPath(), "ConnectOnion-containment");

        Assert.True(AppStorage.IsPathInsideDirectory(Path.Combine(root, "avatars", "a.png"), Path.Combine(root, "avatars")));
        Assert.False(AppStorage.IsPathInsideDirectory(Path.Combine(root, "avatars-backup", "a.png"), Path.Combine(root, "avatars")));
    }

    [Fact]
    public void DataRootInstanceIdentity_NormalizesCaseAndTrailingSeparator()
    {
        var root = Path.Combine(Path.GetTempPath(), "ConnectOnion-profile");

        Assert.Equal(
            DataRootInstanceIdentity.ForPath(root),
            DataRootInstanceIdentity.ForPath(root.ToUpperInvariant() + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void DataRootInstanceIdentity_DifferentProfilesCanRunIndependently()
    {
        var root = Path.GetTempPath();

        Assert.NotEqual(
            DataRootInstanceIdentity.ForPath(Path.Combine(root, "profile-a")),
            DataRootInstanceIdentity.ForPath(Path.Combine(root, "profile-b")));
    }
}
