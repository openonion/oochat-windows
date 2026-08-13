using System.Text;

namespace ConnectOnion.Protocol.Tests;

/// <summary>Encoding tests: PNG/JPEG data URLs, invalid input, and the server's
/// exact size-limit arithmetic (encoded string length, not raw bytes).</summary>
public class DataUrlCodecTests
{
    [Fact]
    public void Encode_Png_ProducesDataUrlWithCorrectPrefix()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var dataUrl = DataUrlCodec.Encode("image/png", bytes);

        Assert.StartsWith("data:image/png;base64,", dataUrl);
        Assert.EndsWith(Convert.ToBase64String(bytes), dataUrl);
    }

    [Fact]
    public void Encode_Jpeg_UsesJpegMimeType()
    {
        var dataUrl = DataUrlCodec.Encode("image/jpeg", new byte[] { 1, 2, 3 });
        Assert.StartsWith("data:image/jpeg;base64,", dataUrl);
    }

    [Fact]
    public void TryDecode_RoundTripsEncodedBytes()
    {
        var original = Encoding.UTF8.GetBytes("hello world");
        var dataUrl = DataUrlCodec.Encode("text/plain", original);

        var ok = DataUrlCodec.TryDecode(dataUrl, out var mime, out var decoded);

        Assert.True(ok);
        Assert.Equal("text/plain", mime);
        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData("not a data url")]
    [InlineData("data:image/png;base64")] // missing comma
    [InlineData("image/png;base64,AAAA")] // missing "data:" prefix
    [InlineData("data:image/png,AAAA")]   // missing ";base64" marker
    public void TryDecode_MalformedInput_ReturnsFalseWithoutThrowing(string input)
    {
        var ok = DataUrlCodec.TryDecode(input, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryDecode_InvalidBase64Payload_ReturnsFalseWithoutThrowing()
    {
        var ok = DataUrlCodec.TryDecode("data:image/png;base64,not-valid-base64!!!", out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryDecode_EmptyString_ReturnsFalse()
    {
        Assert.False(DataUrlCodec.TryDecode("", out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10 * 1024 * 1024)]
    public void EstimateEncodedLength_MatchesActualEncodedStringLength(int rawSize)
    {
        var bytes = new byte[rawSize];
        new Random(42).NextBytes(bytes);
        var actualDataUrl = DataUrlCodec.Encode("application/pdf", bytes);

        var estimate = DataUrlCodec.EstimateEncodedLength(rawSize, "application/pdf");

        // Must be exact (or a safe upper bound) so preflight validation never lets
        // through a file the server's `len(f["data"])` check would reject.
        Assert.True(estimate >= actualDataUrl.Length,
            $"estimate {estimate} must be >= actual encoded length {actualDataUrl.Length}");
    }

    [Fact]
    public void EstimateEncodedLength_TenMegabyteFileExceedsTenMegabyteLimit()
    {
        // Reproduces the server's actual limit check (network/host/config.py:
        // DEFAULT_FILE_LIMITS max_file_size=10 MB, compared against len(f["data"])).
        // A file at exactly the raw 10 MB boundary must be estimated as OVER the
        // 10 MB wire-encoded limit once base64 overhead is included.
        const long tenMb = 10 * 1024 * 1024;
        var estimate = DataUrlCodec.EstimateEncodedLength(tenMb, "application/pdf");

        Assert.True(estimate > tenMb, "base64 overhead must push a raw-10MB file over the 10MB encoded limit");
    }

    [Theory]
    [InlineData("data:image/png;base64,AAAA", ImageSourceKind.DataUrl)]
    [InlineData("data:image/jpeg;base64,AAAA", ImageSourceKind.DataUrl)]
    [InlineData("https://example.com/image.png", ImageSourceKind.HttpUrl)]
    [InlineData("http://example.com/image.png", ImageSourceKind.HttpUrl)]
    [InlineData("file:///C:/secrets.png", ImageSourceKind.Unsupported)]
    [InlineData("javascript:alert(1)", ImageSourceKind.Unsupported)]
    [InlineData("data:text/html;base64,AAAA", ImageSourceKind.Unsupported)]
    [InlineData("", ImageSourceKind.Unsupported)]
    [InlineData(null, ImageSourceKind.Unsupported)]
    public void ClassifyImageSource_OnlySafeImageSchemesAreAccepted(string? uri, ImageSourceKind expected)
    {
        Assert.Equal(expected, DataUrlCodec.ClassifyImageSource(uri));
    }
}
