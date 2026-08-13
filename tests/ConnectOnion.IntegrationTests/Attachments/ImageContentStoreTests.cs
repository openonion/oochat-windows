using ConnectOnion.IntegrationTests.Database;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Services.Attachments;

namespace ConnectOnion.IntegrationTests.Attachments;

[Collection(DatabaseCollection.Name)]
public sealed class ImageContentStoreTests : IAsyncLifetime
{
    private readonly TempDatabaseFixture _fixture;
    private readonly List<string> _createdPaths = new();

    public ImageContentStoreTests(TempDatabaseFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach (var path in _createdPaths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // A failed test cleanup must not hide the assertion that already ran.
            }
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StoreFile_CopiesToContentAddress_AndSurvivesSourceDeletion()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var source = Path.Combine(_fixture.RootDirectory, $"source-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(source, bytes);
        _createdPaths.Add(source);

        var cached = await ImageContentStore.StoreFileAsync(source, "image/png");
        Assert.NotNull(cached);
        _createdPaths.Add(cached!);
        File.Delete(source);

        Assert.StartsWith(AppStorage.ImageCacheDir, cached, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(cached!));
    }

    [Fact]
    public async Task StoreStream_OverLimit_ReturnsNullAndLeavesNoTemporaryFile()
    {
        Directory.CreateDirectory(AppStorage.ImageCacheDir);
        var before = Directory.GetFiles(AppStorage.ImageCacheDir, "*.image.tmp").ToHashSet();
        using var source = new MemoryStream(new byte[] { 1, 2, 3, 4 });

        var cached = await ImageContentStore.StoreStreamAsync(
            source, "image/png", maxBytes: 3);

        Assert.Null(cached);
        var after = Directory.GetFiles(AppStorage.ImageCacheDir, "*.image.tmp").ToHashSet();
        Assert.True(before.SetEquals(after));
    }

    [Fact]
    public async Task StoreBytes_ConcurrentIdenticalWrites_PublishOneFile()
    {
        var bytes = Guid.NewGuid().ToByteArray();

        var paths = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => ImageContentStore.StoreBytesAsync(bytes, "image/png")));

        var path = Assert.Single(paths.Distinct());
        Assert.NotNull(path);
        _createdPaths.Add(path!);
        Assert.True(File.Exists(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path!));
    }
}
