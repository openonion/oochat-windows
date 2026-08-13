using ConnectOnion.WinUIClient.Diagnostics;

namespace ConnectOnion.WinUIClient.UnitTests.Diagnostics;

public sealed class AtomicTextFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ConnectOnion-AtomicTextFileTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteAllText_RetriesWhileReaderPreventsAtomicReplacement()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "startup.json");
        await File.WriteAllTextAsync(path, "old");

        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var retryObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowRetry = new ManualResetEventSlim();
        var write = Task.Run(() => AtomicTextFile.WriteAllText(
            path,
            "new",
            maxAttempts: 100,
            retryDelayMilliseconds: 0,
            retryObserved: _ =>
            {
                retryObserved.TrySetResult(true);
                allowRetry.Wait();
            }));

        try
        {
            await retryObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(write.IsCompleted);
        }
        finally
        {
            try
            {
                await reader.DisposeAsync();
            }
            finally
            {
                allowRetry.Set();
            }
        }

        await write.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
