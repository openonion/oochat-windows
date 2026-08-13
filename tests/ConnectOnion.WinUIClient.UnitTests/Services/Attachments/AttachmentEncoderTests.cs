using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Services.Attachments;

namespace ConnectOnion.WinUIClient.UnitTests.Services.Attachments;

public sealed class AttachmentEncoderTests
{
    [Fact]
    public async Task EncodeToDataUrlAsync_File_RoundTripsExactBytesAndMimeType()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ConnectOnion.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "payload.bin");
        var expected = new byte[] { 0, 1, 2, 127, 128, 255 };
        await File.WriteAllBytesAsync(path, expected);

        try
        {
            var dataUrl = await AttachmentEncoder.EncodeToDataUrlAsync(path, "application/octet-stream");

            Assert.True(DataUrlCodec.TryDecode(dataUrl, out var mimeType, out var actual));
            Assert.Equal("application/octet-stream", mimeType);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EncodeToDataUrlAsync_CancelledToken_ThrowsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AttachmentEncoder.EncodeToDataUrlAsync("not-read.bin", "application/octet-stream", cancellation.Token));
    }

    [Fact]
    public async Task EncodeToDataUrlAsync_FileLargerThanOneChunk_RoundTripsExactBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ConnectOnion.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "large-payload.bin");
        var expected = new byte[(48 * 1024 * 2) + 17];
        Random.Shared.NextBytes(expected);
        await File.WriteAllBytesAsync(path, expected);

        try
        {
            var dataUrl = await AttachmentEncoder.EncodeToDataUrlAsync(path, "application/octet-stream");

            Assert.True(DataUrlCodec.TryDecode(dataUrl, out var mimeType, out var actual));
            Assert.Equal("application/octet-stream", mimeType);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
