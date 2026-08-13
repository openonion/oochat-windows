using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;

namespace ConnectOnion.Protocol.Tests.Fakes;

/// <summary>
/// Real loopback WebSocket host backed by Kestrel. Unlike HttpListener it does not depend on
/// HTTP.sys URL ACLs, and binding port zero avoids the reserve-then-release port race.
/// </summary>
internal sealed class FakeAgentServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri BaseUri { get; }
    public Task Completion => _completion.Task;

    public FakeAgentServer(Func<WebSocket, CancellationToken, Task> script)
    {
        var options = new WebApplicationOptions
        {
            ApplicationName = typeof(FakeAgentServer).Assembly.FullName,
            EnvironmentName = Environments.Development,
        };
        var builder = WebApplication.CreateSlimBuilder(options);
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0, listen =>
                listen.Protocols = HttpProtocols.Http1));

        _app = builder.Build();
        _app.UseWebSockets();
        _app.Run(context => HandleRequestAsync(context, script, _cts.Token));
        _app.StartAsync(_cts.Token).GetAwaiter().GetResult();

        BaseUri = new Uri(_app.Urls.Single(address => address.StartsWith("http://", StringComparison.Ordinal)));
    }

    private async Task HandleRequestAsync(
        HttpContext context,
        Func<WebSocket, CancellationToken, Task> script,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await script(socket, cancellationToken);
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
            throw;
        }
    }

    public static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var text = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Client closed before the expected frame arrived.");
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return text.ToString();
        }
    }

    public static Task SendTextAsync(
        WebSocket socket,
        string json,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        await _app.DisposeAsync();
        _cts.Dispose();
    }
}
