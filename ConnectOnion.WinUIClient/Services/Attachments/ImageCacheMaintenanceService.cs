using System;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Runs the received-image cache's orphan sweep once per app start
/// (see <see cref="ImageCachePruner"/> for what it will and will not delete).
///
/// <para>Once per launch is the right cadence: the only thing that creates orphans is deleting a
/// conversation, and a sweep is cheap — one query plus a directory listing. A periodic timer
/// would add background wake-ups for a directory that changes a handful of times a session.</para>
/// </summary>
public sealed class ImageCacheMaintenanceService : IHostedService, IDisposable
{
    private readonly ConversationRepository _conversations;
    private readonly ILogger<ImageCacheMaintenanceService> _logger;
    private readonly CancellationTokenSource _stopping = new();

    public ImageCacheMaintenanceService(
        ConversationRepository conversations,
        ILogger<ImageCacheMaintenanceService> logger)
    {
        _conversations = conversations;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Deliberately not awaited: startup must not wait on disk maintenance, and nothing in the
        // app depends on the sweep having finished. Its own token is what stops it, so an app
        // closed seconds after launch does not leave it running against a torn-down host.
        _ = Task.Run(
            () => ImageCachePruner.PruneOrphansAsync(_conversations, _logger, ct: _stopping.Token),
            _stopping.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel only — disposal belongs to Dispose, which the host calls afterwards. Disposing
        // the source here would make a second StopAsync (or the sweep's own token read) throw.
        try { _stopping.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();
}
