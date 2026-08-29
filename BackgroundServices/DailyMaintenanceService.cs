using NexusApp.Brokers.AngelOne;
using NexusApp.Interfaces;
using NexusApp.Services;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Runs a housekeeping pass every trading day just before market open (08:30 IST):
///   1. Cleans previous-day artefacts (orders / positions / signals / audit logs / rolled log files).
///   2. Forces a fresh reload of Angel's instrument master so the day's new expiries are picked up.
///
/// Uses IST (UTC+5:30) since the exchange operates in that timezone regardless of
/// where the app is deployed.
/// </summary>
public sealed class DailyMaintenanceService(
    ILogger<DailyMaintenanceService> logger,
    IServiceProvider services) : BackgroundService
{
    private static readonly TimeSpan MaintenanceTimeIst = new(8, 30, 0);
    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330); // +05:30

    private readonly ILogger<DailyMaintenanceService> _logger = logger;
    private readonly IServiceProvider _services = services;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Daily maintenance service started (runs at {Time} IST every day)",
            MaintenanceTimeIst);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNextRun();
            _logger.LogInformation(
                "Next daily maintenance in {Hours:0.0}h at {NextIst:yyyy-MM-dd HH:mm} IST",
                delay.TotalHours, IstNow().Add(delay));

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                await RunMaintenanceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily maintenance run failed; will retry tomorrow");
            }
        }

        _logger.LogInformation("Daily maintenance service stopped");
    }

    private async Task RunMaintenanceAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== Daily maintenance starting ({Now:yyyy-MM-dd HH:mm} IST) ===", IstNow());

        // 1. Purge previous-day artefacts. DataCleanupService is scoped.
        try
        {
            using var scope = _services.CreateScope();
            var cleanup = scope.ServiceProvider.GetRequiredService<DataCleanupService>();
            var result = await cleanup.CleanupAsync(allData: false, ct);
            _logger.LogInformation(
                "Previous-day cleanup: orders={Orders} positions={Positions} signals={Signals} logs={Audit} files={Files} watchlistCleared={Watch}",
                result.OrdersRemoved, result.PositionsRemoved, result.SignalsRemoved, result.AuditLogsRemoved, result.LogFilesRemoved, result.WatchlistCleared);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup step failed");
        }

        // 1b. Purge durable P&L history beyond the configured retention window (default 90 days).
        try
        {
            using var scope = _services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var history = scope.ServiceProvider.GetRequiredService<TradeHistoryService>();
            var retentionDays = await settings.GetSettingAsync<int?>("PnlHistoryRetentionDays") ?? 90;
            var purged = await history.PurgeHistoryOlderThanAsync(retentionDays, ct);
            _logger.LogInformation("P&L history retention purge removed {Count} row(s) (retention={Days}d)", purged, retentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "P&L history retention purge failed");
        }

        // 2. Force a fresh reload of the Angel instrument master (bypasses today-cache).
        try
        {
            var master = _services.GetRequiredService<AngelInstrumentMaster>();
            var cachePath = Path.Combine(AppContext.BaseDirectory, "Data", "angel-instrument-master.json");
            if (File.Exists(cachePath))
            {
                try { File.Delete(cachePath); _logger.LogInformation("Removed cached instrument master file"); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete cached instrument master"); }
            }

            // Reset loaded state so EnsureLoadedAsync re-downloads.
            master.ResetLoadState();
            await master.EnsureLoadedAsync(ct);
            _logger.LogInformation("Instrument master refreshed for the day ({Count} contracts loaded)", master.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Instrument master reload failed");
        }

        // 3. Clear the live WebSocket subscription set so the new trading day starts clean
        //    and does not keep re-subscribing yesterday's (now expired) option tokens.
        try
        {
            var ws = _services.GetService<AngelOneWebSocketClient>();
            ws?.ResetSubscriptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reset WebSocket subscriptions during daily maintenance");
        }

        _logger.LogInformation("=== Daily maintenance complete ===");
    }

    private static DateTime IstNow() => DateTime.UtcNow.Add(IstOffset);

    private static TimeSpan TimeUntilNextRun()
    {
        var istNow = IstNow();
        var todayRun = istNow.Date.Add(MaintenanceTimeIst);
        var next = istNow < todayRun ? todayRun : todayRun.AddDays(1);
        var delay = next - istNow;
        // Guard against clock jumps producing a zero/negative delay.
        return delay <= TimeSpan.Zero ? TimeSpan.FromHours(24) : delay;
    }
}
