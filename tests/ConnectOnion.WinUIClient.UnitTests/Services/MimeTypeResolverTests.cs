using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class MimeTypeResolverTests
{
    [Fact]
    public void Resolve_MeaningfulStorageContentType_TakesPrecedenceOverExtension()
    {
        Assert.Equal("custom/type", MimeTypeResolver.Resolve("custom/type", "photo.png"));
    }

    [Theory]
    [InlineData("photo.PNG", "image/png")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("document.pdf", "application/pdf")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("slides.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    public void Resolve_GenericStorageType_UsesCaseInsensitiveExtensionMap(string fileName, string expected)
    {
        Assert.Equal(expected, MimeTypeResolver.Resolve("application/octet-stream", fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application/octet-stream")]
    public void Resolve_UnknownExtension_ReturnsBinaryFallback(string? storageType)
    {
        Assert.Equal("application/octet-stream", MimeTypeResolver.Resolve(storageType, "archive.unknown"));
    }
}
