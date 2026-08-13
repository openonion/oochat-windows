using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Attachments;

namespace ConnectOnion.WinUIClient.UnitTests.Services.Attachments;

public sealed class AttachmentValidationServiceTests
{
    [Theory]
    [InlineData("image.PNG", AttachmentKind.Image)]
    [InlineData("photo.webp", AttachmentKind.Image)]
    [InlineData("document.pdf", AttachmentKind.File)]
    [InlineData("malware.exe", AttachmentKind.File)]
    public void ClassifyKind_FileName_ReturnsExpectedKind(string fileName, AttachmentKind expected)
    {
        Assert.Equal(expected, AttachmentValidationService.ClassifyKind(fileName));
    }

    [Theory]
    [InlineData("", "File name is empty.")]
    [InlineData("../secret.txt", "Invalid file name.")]
    [InlineData("folder/file.txt", "Invalid file name.")]
    [InlineData("folder\\file.txt", "Invalid file name.")]
    public void ValidateFileName_InvalidName_ReturnsReadableError(string fileName, string expected)
    {
        Assert.Equal(expected, AttachmentValidationService.ValidateFileName(fileName));
    }

    [Fact]
    public void ValidateKindAllowed_DisabledImageAndMissingFileCapability_RejectsKind()
    {
        Assert.Equal(
            "This agent does not accept image input.",
            AttachmentValidationService.ValidateKindAllowed(
                AttachmentKind.Image, new AgentAcceptedInputs(Text: true, Images: false, Files: new AgentFileInputs(10, 2))));
        Assert.Equal(
            "This agent does not accept file input.",
            AttachmentValidationService.ValidateKindAllowed(
                AttachmentKind.File, new AgentAcceptedInputs(Text: true, Images: true, Files: null)));
    }

    [Fact]
    public void ValidateExtension_UnsupportedOrMissingExtension_ReturnsExactType()
    {
        Assert.Equal("Unsupported file type: .exe", AttachmentValidationService.ValidateExtension(AttachmentKind.File, "tool.exe"));
        Assert.Equal("Unsupported file type: (no extension)", AttachmentValidationService.ValidateExtension(AttachmentKind.File, "README"));
        Assert.Null(AttachmentValidationService.ValidateExtension(AttachmentKind.Image, "PHOTO.JPEG"));
    }

    [Fact]
    public void ValidateCount_AtAdvertisedLimit_RejectsAdditionalAttachment()
    {
        var limits = new AgentFileInputs(MaxFileSizeMb: 5, MaxFilesPerRequest: 2);

        Assert.Null(AttachmentValidationService.ValidateCount(1, limits));
        Assert.Equal("Too many attachments (max 2).", AttachmentValidationService.ValidateCount(2, limits));
    }

    [Fact]
    public void ValidateSize_EncodedPayloadExceedsLimit_RejectsBeforeReadingFile()
    {
        var limits = new AgentFileInputs(MaxFileSizeMb: 1, MaxFilesPerRequest: 1);

        Assert.Null(AttachmentValidationService.ValidateSize(700_000, "application/pdf", limits));
        Assert.Equal(
            "File exceeds the agent's 1 MB limit.",
            AttachmentValidationService.ValidateSize(800_000, "application/pdf", limits));
    }

    [Fact]
    public void Validate_MultipleFailures_ReturnsFirstFailureInContractOrder()
    {
        var candidate = new PendingAttachment
        {
            Kind = AttachmentKind.Image,
            FileName = "../bad.exe",
            MimeType = "application/octet-stream",
            SizeBytes = long.MaxValue,
        };
        var accepted = new AgentAcceptedInputs(Text: true, Images: false, Files: null);

        Assert.Equal("This agent does not accept image input.", AttachmentValidationService.Validate(candidate, accepted, 99));
    }
}
