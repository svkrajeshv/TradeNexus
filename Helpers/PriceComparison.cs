namespace NexusApp.Helpers;

/// <summary>
/// Provides the price normalization used for entry and exit threshold checks.
/// Decimal fractions are ignored for these comparisons; actual prices remain
/// unchanged for execution, display, and P&amp;L calculations.
/// </summary>
public static class PriceComparison
{
    public static decimal Normalize(decimal price) => decimal.Truncate(price);

    public static bool IsGreaterThanOrEqual(decimal price, decimal threshold) =>
        Normalize(price) >= Normalize(threshold);

    public static bool IsLessThanOrEqual(decimal price, decimal threshold) =>
        Normalize(price) <= Normalize(threshold);
}
