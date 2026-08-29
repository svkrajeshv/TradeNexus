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
    private static readonly TimeSpan DefaultTimeIst = new(15, 10, 0);
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
            try
            {
                delay = await ComputeDelayAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute next auto square-off time; retrying shortly");
                delay = IdlePoll;
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                // Re-check enablement right before firing (may have changed while waiting).
                if (await IsEnabledAsync(stoppingToken))
                    await RunSquareOffAsync(stoppingToken);
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
    private async Task<TimeSpan> ComputeDelayAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var enabled = await settings.GetSettingAsync<bool?>("AutoSquareOffEnabled") ?? false;
        if (!enabled)
            return IdlePoll;

        var runTime = ParseTimeOrDefault(await settings.GetSettingAsync<string>("AutoSquareOffTime"));

        var istNow = DateTime.UtcNow.ToIst();
        var todayRun = istNow.Date.Add(runTime);
        var next = istNow < todayRun ? todayRun : todayRun.AddDays(1);
        var delay = next - istNow;

        _logger.LogInformation(
            "Next auto square-off scheduled for {Next:yyyy-MM-dd HH:mm} IST (in {Hours:0.0}h)",
            next, delay.TotalHours);

        return delay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay;
    }

    private async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        return await settings.GetSettingAsync<bool?>("AutoSquareOffEnabled") ?? false;
    }

    private async Task RunSquareOffAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<ITradingEngine>();
        var notifications = scope.ServiceProvider.GetService<INotificationService>();

        var openPositionIds = await db.Positions
            .Where(p => p.ClosedAt == null)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (openPositionIds.Count == 0)
        {
            _logger.LogInformation("Auto square-off: no open positions to close");
            return;
        }

        _logger.LogInformation(
            "Auto square-off triggered at {Now:HH:mm} IST — closing {Count} open position(s)",
            DateTime.UtcNow.ToIst(), openPositionIds.Count);

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
            var msg = $@"🔔 AUTO SQUARE-OFF

• Closed: {closed} of {openPositionIds.Count} position(s)"
                + (failed > 0 ? $"\n• Failed: {failed}" : string.Empty)
                + $"\n• Time: {DateTime.UtcNow.ToIstString("hh:mm:ss tt")} IST";
            try { await notifications.SendTelegramCustomNotificationAsync(msg); }
            catch (Exception ex) { _logger.LogDebug(ex, "Auto square-off Telegram notification failed"); }
        }
    }

    private static TimeSpan ParseTimeOrDefault(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= TimeSpan.Zero && parsed < TimeSpan.FromHours(24))
        {
            return parsed;
        }
        return DefaultTimeIst;
    }
}
