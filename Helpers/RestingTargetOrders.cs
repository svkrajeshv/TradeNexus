using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.Helpers;

/// <summary>
/// Helpers for the broker-side "resting target" SELL LIMIT order that is placed
/// automatically when a plain LIMIT entry fills (see OrderSyncService).
///
/// The resting order lets the profit exit happen at the broker even if the app is
/// offline. Because the app also monitors stop-loss locally, the resting order MUST be
/// cancelled before any app-driven exit (SL hit / square-off) so the position is never
/// sold twice.
/// </summary>
public static class RestingTargetOrders
{
    /// <summary>
    /// True when the order is a broker-side resting target exit (Sell LIMIT, MIS).
    /// In-memory counterpart of <see cref="Query"/> for already-loaded rows.
    /// </summary>
    public static bool IsRestingTarget(Order order) =>
        order.Side == OrderSide.Sell &&
        order.OrderType == OrderType.Limit &&
        order.ProductType == ProductType.Mis;

    /// <summary>
    /// Returns the live (Pending/Accepted) broker-side target SELL LIMIT orders for a
    /// symbol on an account. Paper and rejected placeholders are excluded.
    /// </summary>
    public static IQueryable<Order> Query(TradingDbContext db, int accountId, string symbol) =>
        db.Orders.Where(o =>
            o.TradingAccountId == accountId &&
            o.Symbol == symbol &&
            o.Side == OrderSide.Sell &&
            o.OrderType == OrderType.Limit &&
            o.ProductType == ProductType.Mis &&
            (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Accepted) &&
            o.BrokerId != null &&
            !o.BrokerId.StartsWith("Paper") &&
            !o.BrokerId.StartsWith("REJ-"));

    /// <summary>
    /// True when a broker-side target order is currently resting for the symbol.
    /// </summary>
    public static Task<bool> ExistsAsync(TradingDbContext db, int accountId, string symbol, CancellationToken ct = default) =>
        Query(db, accountId, symbol).AnyAsync(ct);

    /// <summary>
    /// Cancels every resting target order for the symbol at the broker and marks the local
    /// rows <see cref="OrderStatus.Cancelled"/>. Returns the number of orders cancelled.
    /// Never throws — a cancel failure must not block the exit itself, but it is logged so
    /// the caller can decide whether to proceed.
    /// </summary>
    public static async Task<int> CancelAsync(
        TradingDbContext db,
        IBroker broker,
        int accountId,
        string symbol,
        ILogger logger,
        CancellationToken ct = default)
    {
        var resting = await Query(db, accountId, symbol).ToListAsync(ct);
        if (resting.Count == 0)
            return 0;

        var cancelled = 0;
        foreach (var order in resting)
        {
            try
            {
                var ok = await broker.CancelOrderAsync(order.BrokerId!);
                if (ok)
                {
                    order.Status = OrderStatus.Cancelled;
                    cancelled++;
                    logger.LogInformation("Cancelled resting target order {BrokerId} for {Symbol}", order.BrokerId, symbol);
                }
                else
                {
                    logger.LogWarning("Broker refused cancel of resting target order {BrokerId} for {Symbol}", order.BrokerId, symbol);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to cancel resting target order {BrokerId} for {Symbol}", order.BrokerId, symbol);
            }
        }

        if (cancelled > 0)
            await db.SaveChangesAsync(ct);

        return cancelled;
    }
}
