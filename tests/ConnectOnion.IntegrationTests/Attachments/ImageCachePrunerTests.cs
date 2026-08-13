using ConnectOnion.IntegrationTests.Database;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Attachments;

namespace ConnectOnion.IntegrationTests.Attachments;

[Collection(DatabaseCollection.Name)]
public sealed class ImageCachePrunerTests : IAsyncLifetime
{
    private readonly TempDatabaseFixture _fixture;
    private readonly ConversationRepository _repository = new();

    public ImageCachePrunerTests(TempDatabaseFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>The whole collection shares one database, and <c>SessionRepository</c> persists
    /// the session index as a whole-table snapshot — so sessions left behind here surface as
    /// phantom rows in <c>SessionRepositoryTests</c>' assertions. Clean up after ourselves:
    /// children first, since the foreign keys declare no <c>ON DELETE</c>.</summary>
    public async ValueTask DisposeAsync()
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM message_attachments WHERE conversation_id LIKE 'prune-%';
            DELETE FROM messages WHERE conversation_id LIKE 'prune-%';
            DELETE FROM sessions WHERE id LIKE 'prune-%';
            """;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task PruneOrphans_DeletesUnreferencedImages_ButKeepsReferencedOnes()
    {
        await _fixture.CreateSessionAsync("prune-basic");
        var referenced = WriteCachedImage("referenced-image.png");
        var orphan = WriteCachedImage("orphan-image.png");

        await _repository.UpsertMessagesAsync("prune-basic", new[]
        {
            AgentMessageWithImage(1, referenced),
        });

        // Both files are older than the age guard, so only the reference set decides.
        // Assertions are on these two files rather than the returned count: every test in this
        // collection shares one cache directory, so a sibling's leftovers would skew a total.
        await ImageCachePruner.PruneOrphansAsync(_repository, now: DateTimeOffset.UtcNow.AddDays(1));

        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task PruneOrphans_LeavesRecentlyWrittenFilesAlone_EvenWithNoReferenceYet()
    {
        await _fixture.CreateSessionAsync("prune-young");
        var justWritten = WriteCachedImage("in-flight-image.png");

        // The real race this guards: the image file is written mid-turn, but the row that
        // references it is not written until the turn is persisted at its end.
        await ImageCachePruner.PruneOrphansAsync(_repository);

        Assert.True(File.Exists(justWritten));
    }

    [Fact]
    public async Task PruneOrphans_ConversationDeleted_ReclaimsItsImages()
    {
        await _fixture.CreateSessionAsync("prune-deleted");
        var image = WriteCachedImage("deleted-conversation-image.png");
        await _repository.UpsertMessagesAsync("prune-deleted", new[] { AgentMessageWithImage(1, image) });

        // Deleting the conversation drops the referencing row but leaves the file behind —
        // exactly the orphan this sweep exists to reclaim.
        await _repository.DeleteMessagesAsync("prune-deleted");

        await ImageCachePruner.PruneOrphansAsync(_repository, now: DateTimeOffset.UtcNow.AddDays(1));

        Assert.False(File.Exists(image));
    }

    /// <summary>A cache file is the only copy of an agent image — the base64 is never persisted —
    /// so a path stored under a different data root (packaged vs unpackaged) must still protect
    /// its file. Matching is on the content-hash file name for exactly this reason.</summary>
    [Fact]
    public async Task PruneOrphans_ReferencePathFromAnotherDataRoot_StillProtectsTheFile()
    {
        await _fixture.CreateSessionAsync("prune-otherroot");
        var image = WriteCachedImage("relocated-image.png");

        await _repository.UpsertMessagesAsync("prune-otherroot", new[]
        {
            AgentMessageWithImage(1, Path.Combine(@"C:\Some\Other\Root\cache\images", "relocated-image.png")),
        });

        await ImageCachePruner.PruneOrphansAsync(_repository, now: DateTimeOffset.UtcNow.AddDays(1));

        Assert.True(File.Exists(image));
    }

    private static string WriteCachedImage(string fileName)
    {
        Directory.CreateDirectory(AppStorage.ImageCacheDir);
        var path = Path.Combine(AppStorage.ImageCacheDir, fileName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private static ChatMessage AgentMessageWithImage(long id, string cachePath) => new()
    {
        Id = id,
        Role = ChatRole.Agent,
        Content = "here is an image",
        Attachments =
        {
            new ChatAttachment
            {
                Id = "attachment-" + id,
                Kind = AttachmentKind.Image,
                FileName = Path.GetFileName(cachePath),
                MimeType = "image/png",
                SizeBytes = 3,
                LocalCachePath = cachePath,
                Status = AttachmentStatus.Sent,
            },
        },
    };
}
