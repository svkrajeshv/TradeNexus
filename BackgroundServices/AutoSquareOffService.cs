using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Helpers;
using NexusApp.Interfaces;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Auto square-off scheduler. Once per trading day, at the configurable
/// <c>AutoSquareOffTime</c> (HH:mm IST), closes every open position across all
/// accounts so intraday exposure is not carried past the chosen cut-off
/// (typically just before the 15:30 IST market close).
///
/// Behaviour is gated by the <c>AutoSquareOffEnabled</c> setting. Both settings
/// are re-read each cycle so changes from the Settings page take effect without
/// an app restart.
/// </summary>
public sealed class AutoSquareOffService(
    ILogger<AutoSquareOffService> logger,
    IServiceProvider services) : BackgroundService
{
    // How long to wait before re-evaluating when the feature is disabled or the
    // time can't be parsed — keeps the loop responsive to setting changes.
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(5);

    private readonly ILogger<AutoSquareOffService> _logger = logger;
    private readonly IServiceProvider _services = services;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auto square-off service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            MarketSegment? segment;
            try
            {
                (delay, segment) = await ComputeDelayAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute next auto square-off time; retrying shortly");
                (delay, segment) = (IdlePoll, null);
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            if (segment is null)
                continue;

            try
            {
                // Re-check enablement right before firing (may have changed while waiting).
                if (await IsEnabledAsync(stoppingToken))
                    await RunSquareOffAsync(segment.Value, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto square-off run failed");
            }
        }

        _logger.LogInformation("Auto square-off service stopped");
    }

    /// <summary>
    /// Returns the delay until the next scheduled square-off. When the feature is
    /// disabled it returns a short idle poll so the loop keeps checking for the
    /// setting being turned on.
    /// </summary>
    private async Task<(TimeSpan Delay, MarketSegment? Segment)> ComputeDelayAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var enabled = await settings.GetSettingAsync<bool?>("AutoSquareOffEnabled") ?? false;
        if (!enabled)
            return (IdlePoll, null);

        var istNow = DateTime.UtcNow.ToIst();
        DateTime? next = null;
        MarketSegment? nextSegment = null;
        foreach (var segment in SegmentTimeSettings.All)
        {
            var runTime = ParseTimeOrDefault(
                await settings.GetSettingAsync<string>(SegmentTimeSettings.AutoSquareOffTimeKey(segment)),
                segment);
            var todayRun = istNow.Date.Add(runTime);
            var candidate = istNow < todayRun ? todayRun : todayRun.AddDays(1);
            if (next is null || candidate < next)
            {
                next = candidate;
                nextSegment = segment;
            }
        }

        var delay = next!.Value - istNow;

        _logger.LogInformation(
            "Next {Segment} auto square-off scheduled for {Next:yyyy-MM-dd HH:mm} IST (in {Hours:0.0}h)",
            SegmentTimeSettings.Label(nextSegment!.Value), next, delay.TotalHours);

        // Wake at least every IdlePoll so setting changes are picked up.
        if (delay > IdlePoll)
            return (IdlePoll, null);

        return (delay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay, nextSegment);
    }

    private async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        return await settings.GetSettingAsync<bool?>("AutoSquareOffEnabled") ?? false;
    }

    private async Task RunSquareOffAsync(MarketSegment segment, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<ITradingEngine>();
        var notifications = scope.ServiceProvider.GetService<INotificationService>();
        var segmentLabel = SegmentTimeSettings.Label(segment);

        var openPositions = await db.Positions
            .Where(p => p.ClosedAt == null)
            .Select(p => new { p.Id, p.Symbol })
            .ToListAsync(ct);
        var openPositionIds = openPositions
            .Where(p => MarketSegments.ForTradingSymbol(p.Symbol) == segment)
            .Select(p => p.Id)
            .ToList();

        if (openPositionIds.Count == 0)
        {
            _logger.LogInformation("Auto square-off ({Segment}): no open positions to close", segmentLabel);
            return;
        }

        _logger.LogInformation(
            "Auto square-off ({Segment}) triggered at {Now:HH:mm} IST — closing {Count} open position(s)",
            segmentLabel, DateTime.UtcNow.ToIst(), openPositionIds.Count);

        var closed = 0;
        var failed = 0;
        foreach (var positionId in openPositionIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await engine.SquareOffPositionAsync(positionId))
                    closed++;
                else
                    failed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Auto square-off failed for position {PositionId}", positionId);
            }
        }

        _logger.LogInformation(
            "Auto square-off complete: closed {Closed}, failed {Failed} of {Total}",
            closed, failed, openPositionIds.Count);

        if (notifications is not null)
        {
            var msg = $@"🔔 AUTO SQUARE-OFF ({segmentLabel})

• Closed: {closed} of {openPositionIds.Count} position(s)"
                + (failed > 0 ? $"\n• Failed: {failed}" : string.Empty)
                + $"\n• Time: {DateTime.UtcNow.ToIstString("hh:mm:ss tt")} IST";
            try { await notifications.SendTelegramCustomNotificationAsync(msg); }
            catch (Exception ex) { _logger.LogDebug(ex, "Auto square-off Telegram notification failed"); }
        }
    }

    private static TimeSpan ParseTimeOrDefault(string? value, MarketSegment segment)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= TimeSpan.Zero && parsed < TimeSpan.FromHours(24))
        {
            return parsed;
        }
        return TimeSpan.Parse(SegmentTimeSettings.DefaultAutoSquareOffTime(segment), System.Globalization.CultureInfo.InvariantCulture);
    }
}
