using NexusApp.Models;

namespace NexusApp.TradingEngine;

/// <summary>
/// Resolves stop-loss / target levels when a segment override is configured: the signal's
/// own level wins when its distance from entry (in points) is smaller than the override
/// points; otherwise the override (measured from <c>basePrice</c>) is used.
/// </summary>
internal static class OverrideRiskLevels
{
    public static decimal ResolveStopLoss(bool isBuy, decimal signalEntry, decimal? signalSl, decimal basePrice, decimal overridePts)
    {
        if (signalSl is > 0 && signalEntry > 0)
        {
            var distance = isBuy ? signalEntry - signalSl.Value : signalSl.Value - signalEntry;
            if (distance > 0 && distance < overridePts)
                return signalSl.Value;
        }

        return isBuy ? Math.Max(0.05m, basePrice - overridePts) : basePrice + overridePts;
    }

    public static List<decimal> ResolveTargets(bool isBuy, decimal signalEntry, IReadOnlyList<decimal>? signalTargets, decimal basePrice, decimal overridePts)
    {
        if (signalTargets is { Count: > 0 } && signalEntry > 0)
        {
            var first = signalTargets[0];
            var distance = isBuy ? first - signalEntry : signalEntry - first;
            if (first > 0 && distance > 0 && distance < overridePts)
                return [.. signalTargets];
        }

        return [isBuy ? basePrice + overridePts : Math.Max(0.05m, basePrice - overridePts)];
    }

    public static bool IsBuy(SignalAction? action) => action is null or SignalAction.Buy;
}
