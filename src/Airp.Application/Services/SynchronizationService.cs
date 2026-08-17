using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain;

namespace Airp.Application.Services;

/// <summary>
/// Keeps the terminal's cached state aligned with the live browser session.
/// </summary>
/// <remarks>
/// <para>
/// Runs as a hosted service so the periodic pass starts and stops with the application. A
/// pass never blocks the UI: views read from <see cref="IChatService.Cached"/> and are
/// pushed an update through <see cref="IChatService.Changed"/> when new data lands.
/// </para>
/// <para>
/// Failures are swallowed into <see cref="SyncCompleted.Error"/> rather than thrown. A
/// background refresh that fails should surface as a status-bar note, not as a crash.
/// </para>
/// </remarks>
public sealed class SynchronizationService : BackgroundService, ISynchronizationService
{
    private readonly IChatService _chats;
    private readonly IOptionsMonitor<AirpOptions> _options;
    private readonly ILogger<SynchronizationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initialises the service.</summary>
    /// <param name="chats">Chat cache to refresh.</param>
    /// <param name="options">Application options, for the refresh interval.</param>
    /// <param name="logger">Logger.</param>
    public SynchronizationService(
        IChatService chats,
        IOptionsMonitor<AirpOptions> options,
        ILogger<SynchronizationService> logger)
    {
        _chats = chats;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<SyncCompleted>? Completed;

    /// <inheritdoc />
    public bool IsSyncing { get; private set; }

    /// <inheritdoc />
    public async Task<SyncCompleted> SyncNowAsync(
        SyncTrigger trigger = SyncTrigger.Manual,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            // A pass is already running; report the current state rather than queueing another.
            return new SyncCompleted(trigger, _chats.Cached.Count, null, DateTimeOffset.UtcNow);
        }

        IsSyncing = true;
        try
        {
            var chats = await _chats.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return Finish(new SyncCompleted(trigger, chats.Count, null, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Synchronisation pass ({Trigger}) failed.", trigger);
            return Finish(new SyncCompleted(trigger, _chats.Cached.Count, ex, DateTimeOffset.UtcNow));
        }
        finally
        {
            IsSyncing = false;
            _gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the UI a moment to draw its first frame before competing for the disk.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = _options.CurrentValue.AutoRefreshInterval;
            if (interval is null)
            {
                // Auto-refresh is off; poll the setting occasionally so it can be re-enabled live.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await SyncNowAsync(SyncTrigger.Timer, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await Task.Delay(interval.Value, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private SyncCompleted Finish(SyncCompleted result)
    {
        Completed?.Invoke(this, result);
        return result;
    }
}
