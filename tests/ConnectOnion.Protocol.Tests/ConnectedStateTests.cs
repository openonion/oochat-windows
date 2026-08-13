namespace ConnectOnion.Protocol.Tests;

/// <summary>
/// Frames here are copied from the shapes <c>establish_connection</c> actually sends
/// (connectonion/network/host/ws_router/connect.py).
/// </summary>
public class ConnectedStateTests
{
    [Fact]
    public void Parse_ReadsPlainConnected()
    {
        var state = ConnectedState.Parse(WireMessage.Parse(
            """{"type":"CONNECTED","session_id":"abc","status":"connected"}"""));

        Assert.Equal("abc", state.SessionId);
        Assert.Equal(ConnectedStatuses.Connected, state.Status);
        Assert.False(state.IsRunning);
        Assert.False(state.ServerNewer);
        Assert.Null(state.SessionJson);
        Assert.Null(state.ChatItemsJson);
    }

    /// <summary>"running" is the status that changes client behaviour: the host has already
    /// rewound to last_msg_id and resumed forwarding, so the open turn continues.</summary>
    [Fact]
    public void Parse_FlagsRunningSession()
    {
        var state = ConnectedState.Parse(WireMessage.Parse(
            """{"type":"CONNECTED","session_id":"abc","status":"running"}"""));

        Assert.True(state.IsRunning);
    }

    [Fact]
    public void Parse_DefaultsMissingStatusToNew()
    {
        var state = ConnectedState.Parse(WireMessage.Parse(
            """{"type":"CONNECTED","session_id":"abc"}"""));

        Assert.Equal(ConnectedStatuses.New, state.Status);
    }

    /// <summary>An unrecognized status is passed through rather than coerced, so a host that
    /// grows a fourth state shows up in logs instead of silently reading as "new".</summary>
    [Fact]
    public void Parse_PreservesUnknownStatus()
    {
        var state = ConnectedState.Parse(WireMessage.Parse(
            """{"type":"CONNECTED","session_id":"abc","status":"draining"}"""));

        Assert.Equal("draining", state.Status);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public void Parse_CapturesServerNewerPayloads()
    {
        var state = ConnectedState.Parse(WireMessage.Parse(
            """
            {"type":"CONNECTED","session_id":"abc","status":"connected","server_newer":true,
             "session":{"messages":[{"role":"user","content":"hi"}]},
             "chat_items":[{"id":"msg-0","type":"user","content":"hi"}]}
            """));

        Assert.True(state.ServerNewer);
        Assert.Contains("\"role\":\"user\"", state.SessionJson);
        Assert.Contains("msg-0", state.ChatItemsJson);
    }

    /// <summary>Without the flag the payloads are not materialized even if present — the raw
    /// text of a full session is large enough that reading it per reconnect would be a real
    /// cost for a string no caller reads.</summary>
    [Fact]
    public void Parse_IgnoresPayloadsWhenServerNewerIsAbsent()
    {
        var state = ConnectedState.Parse(WireMessage.Parse(
            """
            {"type":"CONNECTED","session_id":"abc","status":"connected",
             "session":{"messages":[]},"chat_items":[]}
            """));

        Assert.False(state.ServerNewer);
        Assert.Null(state.SessionJson);
        Assert.Null(state.ChatItemsJson);
    }
}
