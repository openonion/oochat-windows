namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class ProductionDiagnosticOutputContractTests
{
    private static readonly string[] ForbiddenOutputCalls =
    [
        "Console.Write",
        "Debug.Write",
        "Trace.Write",
        "Debugger.Log",
    ];

    [Fact]
    public void ProductionProjects_DoNotWriteToConsoleOrDebuggerOutput()
    {
        var repositoryRoot = FindRepositoryRoot();
        var productionRoots = new[]
        {
            "ConnectOnion.WinUIClient",
            "ConnectOnion.WinUIClient.Core",
            "ConnectOnion.Protocol",
        };

        var violations = productionRoots
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !IsGeneratedOutput(path))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: line, Number: index + 1)))
            .Where(candidate => ForbiddenOutputCalls.Any(candidate.Line.Contains))
            .Select(candidate =>
                $"{Path.GetRelativePath(repositoryRoot, candidate.Path)}:{candidate.Number}: {candidate.Line.Trim()}")
            .ToList();

        Assert.Empty(violations);
    }

    private static bool IsGeneratedOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "ConnectOnion.WinUIClient")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
