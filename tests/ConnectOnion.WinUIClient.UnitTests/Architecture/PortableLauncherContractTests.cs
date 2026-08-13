namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class PortableLauncherContractTests
{
    private static string RepoFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);

        var path = Path.Combine(new[] { directory!.FullName }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Expected to find {path}.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Launcher_UsesANormalBuildGraphAndADedicatedNativeAotReleaseGraph()
    {
        var project = RepoFile("ConnectOnion.PortableLauncher", "ConnectOnion.PortableLauncher.csproj");
        var workflow = RepoFile(".github", "workflows", "release.yml");
        var buildLock = RepoFile("ConnectOnion.PortableLauncher", "packages.lock.json");
        var publishLock = RepoFile("ConnectOnion.PortableLauncher", "packages.publish.lock.json");

        Assert.Contains("<OutputType>WinExe</OutputType>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishAot>false</PublishAot>", project, StringComparison.Ordinal);
        Assert.Contains("<SelfContained>false</SelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishSingleFile>false</PublishSingleFile>", project, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon>..\\ConnectOnion.WinUIClient\\Assets\\app-icon.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains("-p:PublishAot=true", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:SelfContained=true", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=true", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:NuGetLockFilePath=packages.publish.lock.json", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.DotNet.ILCompiler", buildLock, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.NET.ILLink.Tasks", buildLock, StringComparison.Ordinal);
        Assert.Contains("Microsoft.DotNet.ILCompiler", publishLock, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NET.ILLink.Tasks", publishLock, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_OnlyStartsTheFixedNestedApplicationAndForwardsArguments()
    {
        var program = RepoFile("ConnectOnion.PortableLauncher", "Program.cs");

        Assert.Contains("app\\ConnectOnion.WinUIClient.exe", program, StringComparison.Ordinal);
        Assert.Contains("Path.GetFullPath", program, StringComparison.Ordinal);
        Assert.Contains("File.Exists(applicationPath)", program, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(argument)", program, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", program, StringComparison.Ordinal);
    }
}
