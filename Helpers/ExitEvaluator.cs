using NexusApp.Models;

namespace NexusApp.Helpers;

public enum ExitReason
{
    None,
    StopLoss,
    Target
}

public static class ExitEvaluator
{
    public static ExitReason Evaluate(Position position, decimal currentPrice)
    {
        var isLong = position.Signal is null || position.Signal.Action == SignalAction.Buy;

        if (position.StopLoss is > 0 &&
            PriceComparison.Normalize(position.StopLoss.Value) < PriceComparison.Normalize(position.EntryPrice) &&
            (isLong
                ? PriceComparison.IsLessThanOrEqual(currentPrice, position.StopLoss.Value)
                : PriceComparison.IsGreaterThanOrEqual(currentPrice, position.StopLoss.Value)))
        {
            return ExitReason.StopLoss;
        }

        if (position.Targets is not { Count: > 0 })
            return ExitReason.None;

        var targetHit = isLong
            ? position.Targets
                .Where(t => PriceComparison.Normalize(t) > PriceComparison.Normalize(position.EntryPrice))
                .OrderBy(t => t)
                .Any(t => PriceComparison.IsGreaterThanOrEqual(currentPrice, t))
            : position.Targets
                .Where(t => t > 0 && PriceComparison.Normalize(t) < PriceComparison.Normalize(position.EntryPrice))
                .OrderByDescending(t => t)
                .Any(t => PriceComparison.IsLessThanOrEqual(currentPrice, t));

        return targetHit ? ExitReason.Target : ExitReason.None;
    }
}
