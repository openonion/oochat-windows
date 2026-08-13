using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectOnion.WinUIClient.UITests;

/// <summary>
/// A real loopback HTTP/WebSocket agent used by the real-window chat tests. Kestrel binds port
/// zero, avoiding both HTTP.sys URL ACLs and the reserve-then-release port race. The app reaches
/// this through its production <c>HttpClient</c> and <c>ClientWebSocket</c> paths.
/// </summary>
internal sealed class UiFakeAgentServer : IAsyncDisposable
{
    private const string InfoJson = """
        {
          "name": "UI Automation Agent",
          "online": true,
          "accepted_inputs": {
            "text": true,
            "images": true,
            "files": { "max_file_size_mb": 10, "max_files_per_request": 5 }
          }
        }
        """;

    private readonly WebApplication _app;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<int, WebSocket, CancellationToken, Task> _socketScript;
    private readonly ConcurrentQueue<Exception> _faults = new();
    private int _connectionCount;

    public Uri BaseUri { get; }
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public UiFakeAgentServer(Func<int, WebSocket, CancellationToken, Task>? socketScript = null)
    {
        _socketScript = socketScript ?? HoldSocketOpenAsync;
        var options = new WebApplicationOptions
        {
            ApplicationName = typeof(UiFakeAgentServer).Assembly.FullName,
            EnvironmentName = Environments.Development,
        };
        var builder = WebApplication.CreateSlimBuilder(options);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));

        _app = builder.Build();
        _app.UseWebSockets();
        _app.Run(HandleRequestAsync);
        _app.StartAsync(_cts.Token).GetAwaiter().GetResult();
        BaseUri = new Uri(_app.Urls.Single(value =>
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (context.Request.Path.Equals("/health"))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("ok", _cts.Token);
            return;
        }

        if (context.Request.Path.Equals("/info"))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(InfoJson, _cts.Token);
            return;
        }

        if (!context.Request.Path.Equals("/ws") || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var connection = Interlocked.Increment(ref _connectionCount);
            await _socketScript(connection, socket, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (WebSocketException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _faults.Enqueue(ex);
        }
    }

    public void ThrowIfFaulted()
    {
        if (_faults.TryDequeue(out var fault))
            throw new InvalidOperationException("The UI fake agent failed.", fault);
    }

    public static async Task<string> ReceiveTextAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("The client closed before the expected frame arrived.");
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return text.ToString();
        }
    }

    public static async Task<string> ReceiveFrameOfTypeAsync(
        WebSocket socket,
        string expectedType,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var json = await ReceiveTextAsync(socket, cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), expectedType, StringComparison.Ordinal))
                return json;
        }
    }

    public static Task SendTextAsync(
        WebSocket socket,
        string json,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task HoldSocketOpenAsync(
        int _,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        await ReceiveFrameOfTypeAsync(socket, "CONNECT", cancellationToken);
        await SendTextAsync(
            socket,
            "{\"type\":\"CONNECTED\",\"session_id\":\"ui-session\",\"status\":\"connected\"}",
            cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _app.StopAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
        await _app.DisposeAsync();
        _cts.Dispose();
    }
}
