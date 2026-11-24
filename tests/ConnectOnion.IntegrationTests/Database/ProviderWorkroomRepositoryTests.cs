using System.Text.Json;
using System.Text.Json.Nodes;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.IntegrationTests.Database;

[Collection(DatabaseCollection.Name)]
public sealed class ProviderWorkRoomRepositoryTests(TempDatabaseFixture database)
{
    private readonly ProviderWorkRoomRepository _repository = new();

    private static AgentStreamEvent Event(string type, string json) => new(type, "", null, json);

    private static string Invocation(string roomId) => new JsonObject
    {
        ["workroomId"] = roomId,
        ["provider"] = "codex",
        ["invocationId"] = "inv-1",
        ["parentToolCallId"] = "call-1",
        ["title"] = "Refactor the parser",
        ["status"] = "running",
        ["stateRevision"] = 1,
        ["capabilities"] = new JsonArray("provider_continuation"),
        ["providerPermission"] = JsonNode.Parse(
            """{"activeOptionId":"read","effectiveRevision":2,"options":[{"id":"read","selectable":true},{"id":"write","selectable":true}]}"""),
    }.ToJsonString();

    private static string WithRoom(string roomId, string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node["workroomId"] = roomId;
        return node.ToJsonString();
    }

    private async Task<(string Conversation, string Room)> NewRoomAsync()
    {
        var conversation = "conv-" + Guid.NewGuid().ToString("N");
        var room = "wr-" + Guid.NewGuid().ToString("N");
        await database.CreateSessionAsync(conversation);
        Assert.True(await _repository.ApplyEventAsync(conversation, Event("provider_invocation", Invocation(room))));
        return (conversation, room);
    }

    [Fact]
    public async Task ApplyEventAsync_ValidInvocation_PersistsRoom()
    {
        var (conversation, roomId) = await NewRoomAsync();

        var room = await new ProviderWorkRoomRepository().GetAsync(roomId);

        Assert.NotNull(room);
        Assert.Equal(conversation, room.ParentConversationId);
        Assert.Equal("Refactor the parser", room.Title);
        Assert.Equal("inv-1", room.CurrentInvocationId);
        Assert.True(room.CanContinue);
    }

    [Theory]
    [InlineData("tool_call", """{"workroomId":"wr-x"}""")]
    [InlineData("provider_invocation", "{not json")]
    [InlineData("provider_activity", """{"files":["a.txt"]}""")]
    [InlineData("provider_message", """{"messageId":"m1","role":"assistant","text":"hi"}""")]
    [InlineData("provider_message", """{"invocationId":"inv-nobody","messageId":"m1","role":"assistant","text":"hi"}""")]
    [InlineData("provider_invocation", """{"workroomId":"wr\u0001bad","stateRevision":1}""")]
    public async Task ApplyEventAsync_InvalidOrUnmatchableEvent_ReturnsFalse(string type, string json)
    {
        var conversation = "conv-" + Guid.NewGuid().ToString("N");
        await database.CreateSessionAsync(conversation);

        Assert.False(await _repository.ApplyEventAsync(conversation, Event(type, json)));
    }

    [Fact]
    public async Task ProviderMessage_WithoutRoomId_CorrelatesByInvocationId()
    {
        var (conversation, roomId) = await NewRoomAsync();

        Assert.True(await _repository.ApplyEventAsync(conversation, Event("provider_message",
            """{"invocationId":"inv-1","messageId":"m1","role":"assistant","text":"hi"}""")));

        Assert.Single((await _repository.GetAsync(roomId))!.Messages);
    }

    [Theory]
    [InlineData("""{"invocationId":"inv-1","messageId":"m1","role":"assistant","text":"hi"}""")]
    [InlineData("""{"parentToolCallId":"call-1","messageId":"m1","role":"assistant","text":"hi"}""")]
    public async Task ProviderMessage_AfterRestart_CorrelatesFromSavedRoom(string json)
    {
        var (conversation, roomId) = await NewRoomAsync();
        var restarted = new ProviderWorkRoomRepository(); // empty in-memory correlation map

        Assert.True(await restarted.ApplyEventAsync(conversation, Event("provider_message", json)));

        Assert.Single((await restarted.GetAsync(roomId))!.Messages);
    }

    [Fact]
    public async Task ApplyEventAsync_FromDifferentConversation_CannotUpdateRoom()
    {
        var (conversationA, roomId) = await NewRoomAsync();
        var conversationB = "conv-" + Guid.NewGuid().ToString("N");
        await database.CreateSessionAsync(conversationB);

        Assert.False(await _repository.ApplyEventAsync(conversationB, Event("provider_phase",
            WithRoom(roomId, """{"phase":"completed","stateRevision":9}"""))));

        var room = await _repository.GetAsync(roomId);
        Assert.Equal(conversationA, room!.ParentConversationId);
        Assert.Equal("running", room.Status);
    }

    [Fact]
    public async Task ApplyEventAsync_NotifiesWithSnapshotThatCannotMutateRepository()
    {
        var (conversation, roomId) = await NewRoomAsync();
        var updates = new List<ProviderWorkRoom>();
        _repository.WorkRoomUpdated += updates.Add;

        await _repository.ApplyEventAsync(conversation, Event("provider_phase", WithRoom(roomId, """{"phase":"thinking","stateRevision":2}""")));
        updates[0].Title = "tampered";

        Assert.Equal("thinking", Assert.Single(updates).Status);
        Assert.Equal("Refactor the parser", (await _repository.GetAsync(roomId))!.Title);
    }

    [Fact]
    public async Task SetPermissionChangeUnavailableAsync_PersistsAndNotifiesOnlyOnChange()
    {
        var (_, roomId) = await NewRoomAsync();
        var updates = new List<ProviderWorkRoom>();
        _repository.WorkRoomUpdated += updates.Add;

        await _repository.SetPermissionChangeUnavailableAsync(roomId, true);
        await _repository.SetPermissionChangeUnavailableAsync(roomId, true);
        await _repository.SetPermissionChangeUnavailableAsync("wr-missing-" + Guid.NewGuid().ToString("N"), true);

        Assert.Single(updates);
        Assert.True((await new ProviderWorkRoomRepository().GetAsync(roomId))!.PermissionChangeUnavailable);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesTheWholeRoom()
    {
        var (conversation, roomId) = await NewRoomAsync();
        ProviderWorkRoom? last = null;
        _repository.WorkRoomUpdated += room => last = room;

        foreach (var (type, json) in new[]
        {
            ("provider_message", """{"messageId":"m2","role":"assistant","text":"second","sequence":2,"author":"codex"}"""),
            ("provider_message", """{"messageId":"m1","role":"user","text":"first","sequence":1,"clientMessageId":"c-1"}"""),
            ("provider_activity", """{"activity":{"files":["src/b.cs","src/a.cs"],"status":"modified"}}"""),
            ("provider_approval", """{"requestId":"ap-1","summary":"Run tests","allowedOptions":["approve","reject"],"stateRevision":3}"""),
            ("provider_permission_ack", """{"requestId":"ack-1","accepted":false,"optionId":"write"}"""),
        })
        {
            Assert.True(await _repository.ApplyEventAsync(conversation, Event(type, WithRoom(roomId, json))));
        }

        var loaded = await new ProviderWorkRoomRepository().GetAsync(roomId);

        Assert.Equal(
            JsonSerializer.Serialize(last, AppJsonContext.Default.ProviderWorkRoom),
            JsonSerializer.Serialize(loaded, AppJsonContext.Default.ProviderWorkRoom));
        Assert.Equal(new[] { "m1", "m2" }, loaded!.Messages.Select(message => message.Id));
        Assert.Equal(new[] { "src/b.cs", "src/a.cs" }, loaded.Files.Select(file => file.Path));
        Assert.Equal(new[] { "approve", "reject" }, Assert.Single(loaded.Approvals).AllowedOptions);
        Assert.Equal(new[] { "ack-1" }, loaded.AppliedPermissionAckIds);
    }
}

