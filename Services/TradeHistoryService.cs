using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusApp.Data;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// Archives closed <see cref="Position"/> rows into the durable <see cref="ClosedTradeHistory"/>
/// table so P&amp;L history survives the daily/session cleanup that deletes positions.
/// Archiving is idempotent — positions already present in the history (by PositionId) are skipped.
/// </summary>
public sealed class TradeHistoryService
{
    private const string UnknownChannel = "Unknown";
    private const string UnknownTerminal = "Unknown";

    private readonly IDbContextFactory<TradingDbContext> _dbFactory;
    private readonly ILogger<TradeHistoryService> _logger;

    public TradeHistoryService(
        IDbContextFactory<TradingDbContext> dbFactory,
        ILogger<TradeHistoryService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Archives every closed position that is not yet in the history table.
    /// Returns the number of newly archived rows.
    /// </summary>
    public async Task<int> ArchiveClosedPositionsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await ArchiveAsync(db, saveChanges: true, ct);
    }

    /// <summary>
    /// Deletes durable history rows whose <see cref="ClosedTradeHistory.ClosedAt"/> is older
    /// than <paramref name="retentionDays"/> days. Default retention is 90 days.
    /// Returns the number of rows removed.
    /// </summary>
    public async Task<int> PurgeHistoryOlderThanAsync(int retentionDays = 90, CancellationToken ct = default)
    {
        if (retentionDays <= 0)
            return 0;

        var cutoffUtc = DateTime.UtcNow.Date.AddDays(-retentionDays);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var removed = await db.ClosedTradeHistory
            .Where(h => h.ClosedAt < cutoffUtc)
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
            _logger.LogInformation(
                "Purged {Count} trade history row(s) older than {Days} days (before {Cutoff:yyyy-MM-dd})",
                removed, retentionDays, cutoffUtc);

        return removed;
    }

    /// <summary>
    /// Archives closed positions using a caller-supplied context (used inside cleanup
    /// so archiving and deletion share one flow). Does not save — the caller saves.
    /// Returns the number of new archive entities added to the context.
    /// </summary>
    public async Task<int> ArchiveClosedPositionsAsync(TradingDbContext db, CancellationToken ct = default)
    {
        return await ArchiveAsync(db, saveChanges: false, ct);
    }

    private async Task<int> ArchiveAsync(TradingDbContext db, bool saveChanges, CancellationToken ct)
    {
        // Pull closed positions joined to signal (channel) and account (terminal).
        var closed = await (
            from p in db.Positions.AsNoTracking()
            join s in db.TradingSignals.AsNoTracking() on p.SignalId equals s.Id into sj
            from s in sj.DefaultIfEmpty()
            join a in db.TradingAccounts.AsNoTracking() on p.TradingAccountId equals a.Id into aj
            from a in aj.DefaultIfEmpty()
            where p.ClosedAt != null
            select new
            {
                p.Id,
                p.Symbol,
                p.Quantity,
                p.EntryPrice,
                p.ClosingPrice,
                p.RealizedPnL,
                ChannelName = s != null ? s.ChannelName : null,
                TerminalName = a != null ? a.Name : null,
                ClientId = a != null ? a.ClientId : null,
                p.OpenedAt,
                p.ClosedAt
            }).ToListAsync(ct);

        if (closed.Count == 0)
            return 0;

        var existingIds = await db.ClosedTradeHistory
            .AsNoTracking()
            .Select(h => h.PositionId)
            .ToHashSetAsync(ct);

        var now = DateTime.UtcNow;
        var toAdd = new List<ClosedTradeHistory>();

        foreach (var p in closed)
        {
            if (existingIds.Contains(p.Id))
                continue;

            toAdd.Add(new ClosedTradeHistory
            {
                PositionId = p.Id,
                Symbol = p.Symbol,
                Quantity = p.Quantity,
                EntryPrice = p.EntryPrice,
                ExitPrice = p.ClosingPrice,
                RealizedPnL = p.RealizedPnL ?? 0m,
                ChannelName = string.IsNullOrWhiteSpace(p.ChannelName) ? UnknownChannel : p.ChannelName!,
                TerminalName = string.IsNullOrWhiteSpace(p.TerminalName) ? UnknownTerminal : p.TerminalName!,
                IsPaper = string.Equals(p.ClientId, "PAPER", StringComparison.OrdinalIgnoreCase),
                OpenedAt = p.OpenedAt,
                ClosedAt = p.ClosedAt!.Value,
                ArchivedAt = now
            });
        }

        if (toAdd.Count == 0)
            return 0;

        db.ClosedTradeHistory.AddRange(toAdd);

        if (saveChanges)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Archived {Count} closed trade(s) to history", toAdd.Count);
        }

        return toAdd.Count;
    }
}
