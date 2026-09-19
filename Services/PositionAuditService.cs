using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Helpers;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// One row of the contamination report for a single closed live position.
/// </summary>
/// <param name="PositionId">Primary key of the suspect <see cref="Position"/>.</param>
/// <param name="Symbol">Contract symbol.</param>
/// <param name="Quantity">Position quantity as recorded locally.</param>
/// <param name="EntryPrice">Recorded entry price.</param>
/// <param name="ClosingPrice">Recorded closing price (fabricated when suspect).</param>
/// <param name="RecordedPnl">The <see cref="Position.RealizedPnL"/> currently stored.</param>
/// <param name="ClosedAt">When the row was marked closed.</param>
/// <param name="Reason">Why this row is considered unverified.</param>
public sealed record SuspectClose(
    int PositionId,
    string Symbol,
    decimal Quantity,
    decimal EntryPrice,
    decimal? ClosingPrice,
    decimal? RecordedPnl,
    DateTime? ClosedAt,
    string Reason);

/// <summary>
/// Aggregate result of an audit run.
/// </summary>
public sealed record PositionAuditReport(
    IReadOnlyList<SuspectClose> Suspects,
    decimal SuspectPnlTotal,
    decimal VerifiedPnlTotal,
    int VerifiedCount);

/// <summary>
/// Detects and repairs live <see cref="Position"/> rows that were closed by the paper
/// simulator rather than by a real broker fill.
///
/// <para>
/// Background: <c>PaperTradingEngine.UpdatePositionPricesAsync</c> previously selected
/// every open position instead of only paper ones, so it invented stop-loss/target exits
/// for live positions. Those rows carry a fabricated <see cref="Position.ClosedAt"/>,
/// <see cref="Position.ClosingPrice"/> and <see cref="Position.RealizedPnL"/> that the
/// broker never booked, which inflates realised P&amp;L and corrupts the daily-loss limits
/// in <c>RiskManager</c>.
/// </para>
///
/// <para>
/// A genuine live close always has a matching executed SELL <see cref="Order"/> carrying a
/// real broker id. Simulated closes have no such order, which is the signal used here.
/// </para>
/// </summary>
public sealed class PositionAuditService(
    TradingDbContext context,
    ILogger<PositionAuditService> logger)
{
    private readonly TradingDbContext _context = context;
    private readonly ILogger<PositionAuditService> _logger = logger;

    /// <summary>
    /// Broker ids the application generates for simulated or rejected orders. These are
    /// placeholders, not real exchange references, so an order carrying one does not
    /// prove a broker fill occurred.
    /// </summary>
    private static readonly string[] SyntheticBrokerIdPrefixes = ["Paper", "REJ-"];

    /// <summary>
    /// Lists closed live positions that have no corresponding real broker SELL fill.
    /// Read-only: makes no modifications.
    /// </summary>
    /// <param name="fromUtc">Optional inclusive lower bound on <see cref="Position.ClosedAt"/>.</param>
    public async Task<PositionAuditReport> FindUnverifiedClosesAsync(
        DateTime? fromUtc = null,
        CancellationToken ct = default)
    {
        var closedLive = await _context.Positions
            .AsNoTracking()
            .Include(p => p.TradingAccount)
            .Where(p => p.ClosedAt != null)
            .Where(p => fromUtc == null || p.ClosedAt >= fromUtc)
            .Where(p => !EF.Functions.Like(p.TradingAccount.ClientId, BookScope.PaperClientId))
            .ToListAsync(ct);

        if (closedLive.Count == 0)
            return new PositionAuditReport([], 0m, 0m, 0);

        var accountIds = closedLive.Select(p => p.TradingAccountId).Distinct().ToList();
        var symbols = closedLive.Select(p => p.Symbol).Distinct().ToList();

        // Candidate exit fills. Status/side are filtered in the database; the broker-id
        // credibility test runs in memory because it is a prefix-exclusion set.
        var sellFills = await _context.Orders
            .AsNoTracking()
            .Where(o => o.Side == OrderSide.Sell)
            .Where(o => o.Status == OrderStatus.Executed || o.Status == OrderStatus.Accepted)
            .Where(o => accountIds.Contains(o.TradingAccountId))
            .Where(o => symbols.Contains(o.Symbol))
            .Select(o => new { o.TradingAccountId, o.Symbol, o.BrokerId, o.CreatedAt })
            .ToListAsync(ct);

        var verifiedKeys = sellFills
            .Where(o => IsRealBrokerId(o.BrokerId))
            .Select(o => (o.TradingAccountId, o.Symbol))
            .ToHashSet();

        var suspects = new List<SuspectClose>();
        var verifiedTotal = 0m;
        var verifiedCount = 0;

        foreach (var p in closedLive)
        {
            if (verifiedKeys.Contains((p.TradingAccountId, p.Symbol)))
            {
                verifiedTotal += p.RealizedPnL ?? 0m;
                verifiedCount++;
                continue;
            }

            suspects.Add(new SuspectClose(
                p.Id, p.Symbol, p.Quantity, p.EntryPrice, p.ClosingPrice,
                p.RealizedPnL, p.ClosedAt,
                "Closed with no executed SELL order carrying a real broker id - "
                + "consistent with a simulated exit written by the paper engine."));
        }

        var report = new PositionAuditReport(
            suspects,
            suspects.Sum(s => s.RecordedPnl ?? 0m),
            verifiedTotal,
            verifiedCount);

        _logger.LogWarning(
            "Position audit: {SuspectCount} unverified live closes totalling {SuspectPnl:N2}; "
            + "{VerifiedCount} verified closes totalling {VerifiedPnl:N2}",
            report.Suspects.Count, report.SuspectPnlTotal,
            report.VerifiedCount, report.VerifiedPnlTotal);

        return report;
    }

    /// <summary>
    /// Neutralises the fabricated P&amp;L on unverified closes by setting
    /// <see cref="Position.RealizedPnL"/> to zero, leaving the rows closed.
    ///
    /// <para>
    /// Zero is used deliberately rather than a guess: the true figure is only knowable
    /// from the broker, and substituting an estimate would repeat the original mistake of
    /// presenting derived data as booked fact. The audit log records the discarded value.
    /// </para>
    ///
    /// <para>
    /// The authoritative realised total for the live book comes from the AngelOne position
    /// book, so zeroing these rows removes the phantom profit without losing real history.
    /// </para>
    /// </summary>
    /// <returns>The number of positions corrected.</returns>
    public async Task<int> QuarantineUnverifiedClosesAsync(
        DateTime? fromUtc = null,
        CancellationToken ct = default)
    {
        var report = await FindUnverifiedClosesAsync(fromUtc, ct);
        if (report.Suspects.Count == 0)
            return 0;

        var ids = report.Suspects.Select(s => s.PositionId).ToList();
        var rows = await _context.Positions.Where(p => ids.Contains(p.Id)).ToListAsync(ct);

        foreach (var row in rows)
        {
            var discarded = row.RealizedPnL ?? 0m;

            row.RealizedPnL = 0m;
            row.UnrealizedPnL = 0m;
            row.UnrealizedPnLPercentage = 0m;

            _context.AuditLogs.Add(new AuditLog
            {
                EventType = "DataCorrection",
                Action = "PositionPnlQuarantined",
                EntityType = nameof(Position),
                EntityId = row.Id,
                OldValue = discarded.ToString("N2"),
                NewValue = "0.00",
                Details =
                    $"Discarded simulated realised P&L {discarded:N2} for {row.Symbol} "
                    + $"(entry {row.EntryPrice:N2}, close {row.ClosingPrice:N2}). "
                    + "No executed SELL order with a real broker id backed this close.",
                Timestamp = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Quarantined {Count} unverified live closes, removing {Total:N2} of simulated realised P&L",
            rows.Count, report.SuspectPnlTotal);

        return rows.Count;
    }

    private static bool IsRealBrokerId(string? brokerId)
    {
        if (string.IsNullOrWhiteSpace(brokerId))
            return false;

        foreach (var prefix in SyntheticBrokerIdPrefixes)
        {
            if (brokerId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
