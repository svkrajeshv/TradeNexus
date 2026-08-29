namespace NexusApp.Helpers;

/// <summary>
/// Centralised, direction-aware realised P&amp;L calculation so every close path
/// (paper SL/target, manual close, square-off, live fills) uses one formula.
///
/// For a long position (bought option / BUY): P&amp;L = (exit - entry) * qty.
/// For a short position (written option / SELL): P&amp;L = (entry - exit) * qty.
///
/// The application is currently long-only, so <paramref name="isShort"/> defaults to
/// false and existing behaviour is preserved. Short support only needs callers to pass
/// <c>isShort: true</c> once positions can be opened on the SELL side.
/// </summary>
public static class PnlCalculator
{
    /// <summary>
    /// Computes realised P&amp;L for a closed position.
    /// </summary>
    /// <param name="entryPrice">Fill price when the position was opened.</param>
    /// <param name="exitPrice">Fill price when the position was closed.</param>
    /// <param name="quantity">Absolute quantity (always positive).</param>
    /// <param name="isShort">True for a short/written position; false (default) for long.</param>
    public static decimal RealizedPnl(decimal entryPrice, decimal exitPrice, decimal quantity, bool isShort = false)
    {
        var perUnit = isShort ? entryPrice - exitPrice : exitPrice - entryPrice;
        return perUnit * quantity;
    }

    /// <summary>
    /// Computes unrealised (mark-to-market) P&amp;L for an open position.
    /// </summary>
    public static decimal UnrealizedPnl(decimal entryPrice, decimal currentPrice, decimal quantity, bool isShort = false)
        => RealizedPnl(entryPrice, currentPrice, quantity, isShort);
}
