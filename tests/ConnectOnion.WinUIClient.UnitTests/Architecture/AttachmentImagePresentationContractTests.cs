namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class AttachmentImagePresentationContractTests
{
    [Fact]
    public void AttachmentTemplate_UsesCoherentMutuallyExclusiveImageState()
    {
        var xaml = ReadRepositoryFile("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml");

        Assert.Contains(
            "{Binding IsFailed, Mode=OneWay, Converter={StaticResource BoolToVis}, ConverterParameter=invert}",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "{Binding IsFailed, Mode=OneWay, Converter={StaticResource BoolToVis}}",
            xaml,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }
}
