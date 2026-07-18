using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusApp.Data;
using NexusApp.Interfaces;

namespace NexusApp.Services;

/// <summary>
/// Removes previous-day data (signals, orders, positions, audit logs, watchlist state) and
/// rolled log files so a fresh trading session starts with a clean slate. Today's rows and
/// the currently active log file are preserved so an in-flight session is not disrupted.
/// </summary>
public sealed class DataCleanupService
{
    private readonly IDbContextFactory<TradingDbContext> _dbFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<DataCleanupService> _logger;
    private readonly IWebHostEnvironment _env;

    public DataCleanupService(
        IDbContextFactory<TradingDbContext> dbFactory,
        ISettingsService settings,
        ILogger<DataCleanupService> logger,
        IWebHostEnvironment env)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _logger = logger;
        _env = env;
    }

    public sealed record CleanupResult(
        int SignalsRemoved,
        int OrdersRemoved,
        int PositionsRemoved,
        int AuditLogsRemoved,
        int LogFilesRemoved,
        bool WatchlistCleared);

    /// <summary>
    /// Deletes stored trading data.  When <paramref name="allData"/> is true every row is
    /// removed (regardless of date). Otherwise only rows older than today (local date) are
    /// removed, along with any log file whose modified timestamp is before today.
    /// </summary>
    public async Task<CleanupResult> CleanupAsync(bool allData = false, CancellationToken ct = default)
    {
        var todayLocalStart = DateTime.Today;                        // local midnight
        var cutoffUtc = todayLocalStart.ToUniversalTime();           // rows with CreatedAt/timestamps < this are "yesterday or older"

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        int ordersRemoved;
        int positionsRemoved;
        int signalsRemoved;
        int auditsRemoved;

        if (allData)
        {
            ordersRemoved    = await db.Orders.ExecuteDeleteAsync(ct);
            positionsRemoved = await db.Positions.ExecuteDeleteAsync(ct);
            signalsRemoved   = await db.TradingSignals.ExecuteDeleteAsync(ct);
            auditsRemoved    = await db.AuditLogs.ExecuteDeleteAsync(ct);
        }
        else
        {
            // Delete Orders first (they FK to Signals + Accounts).
            ordersRemoved = await db.Orders
                .Where(o => o.CreatedAt < cutoffUtc)
                .ExecuteDeleteAsync(ct);

            // Closed positions from prior days.
            positionsRemoved = await db.Positions
                .Where(p => p.ClosedAt != null && p.ClosedAt < cutoffUtc)
                .ExecuteDeleteAsync(ct);

            // Signals from prior days AND with no remaining orders pointing at them.
            signalsRemoved = await db.TradingSignals
                .Where(s => s.ReceivedTimestamp < cutoffUtc && !db.Orders.Any(o => o.SignalId == s.Id))
                .ExecuteDeleteAsync(ct);

            auditsRemoved = await db.AuditLogs
                .Where(a => a.Timestamp < cutoffUtc)
                .ExecuteDeleteAsync(ct);
        }

        // Clear the watchlist "hidden signals" set — it references signal Ids that may be gone.
        var watchlistCleared = false;
        try
        {
            await _settings.SetSettingAsync("Watchlist.HiddenSignalIds", string.Empty);
            watchlistCleared = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clear watchlist hidden signals setting");
        }

        // Purge rolled log files (Serilog rolls daily: trading-app-YYYYMMDD.txt).
        var logsRemoved = 0;
        try
        {
            var logsDir = Path.Combine(_env.ContentRootPath, "Logs");
            if (Directory.Exists(logsDir))
            {
                var todayTag = DateTime.Today.ToString("yyyyMMdd");
                foreach (var file in Directory.EnumerateFiles(logsDir, "trading-app-*.txt"))
                {
                    var name = Path.GetFileNameWithoutExtension(file); // e.g. trading-app-20260711
                    var isToday = name.EndsWith(todayTag, StringComparison.OrdinalIgnoreCase);
                    if (allData || (!isToday && File.GetLastWriteTime(file) < todayLocalStart))
                    {
                        try
                        {
                            File.Delete(file);
                            logsRemoved++;
                        }
                        catch (IOException) { /* held by Serilog — skip */ }
                        catch (UnauthorizedAccessException) { /* skip */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up log files");
        }

        _logger.LogInformation(
            "Cleanup done (allData={AllData}): signals={S}, orders={O}, positions={P}, audits={A}, logs={L}",
            allData, signalsRemoved, ordersRemoved, positionsRemoved, auditsRemoved, logsRemoved);

        return new CleanupResult(signalsRemoved, ordersRemoved, positionsRemoved, auditsRemoved, logsRemoved, watchlistCleared);
    }
}
