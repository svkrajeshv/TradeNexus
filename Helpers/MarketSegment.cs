using System.Linq;

namespace NexusApp.Helpers;

/// <summary>
/// The exchange segment an underlying belongs to. Segment drives session hours,
/// exchange routing and the default risk profile, all of which differ sharply
/// between equity index options and commodity options.
/// </summary>
public enum MarketSegment
{
    /// <summary>NSE/BSE index options (NFO / BFO), 09:15-15:30 IST.</summary>
    Equity = 0,

    /// <summary>MCX commodity options, 09:00-23:30 IST.</summary>
    Commodity = 1
}

/// <summary>
/// Resolves an underlying name to its <see cref="MarketSegment"/>.
/// <para>
/// Only the commodities listed in <see cref="CommodityUnderlyings"/> are treated as
/// MCX; everything else - including every existing index - resolves to
/// <see cref="MarketSegment.Equity"/>, so pre-existing behaviour is unchanged.
/// </para>
/// </summary>
public static class MarketSegments
{
    /// <summary>
    /// The MCX commodities supported for options trading. Deliberately a small
    /// whitelist rather than "all of MCX": the instrument master is filtered against
    /// this set, and admitting every commodity would undo the ~90% memory reduction
    /// that filter exists to provide.
    /// </summary>
    public static readonly IReadOnlySet<string> CommodityUnderlyings =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CRUDEOIL", "NATURALGAS", "GOLD", "SILVER"
        };

    /// <summary>
    /// Returns the segment for an underlying, defaulting to
    /// <see cref="MarketSegment.Equity"/> for anything unrecognised.
    /// </summary>
    public static MarketSegment ForUnderlying(string? underlying) =>
        !string.IsNullOrWhiteSpace(underlying) && CommodityUnderlyings.Contains(underlying.Trim())
            ? MarketSegment.Commodity
            : MarketSegment.Equity;

    /// <summary>True when the underlying is one of the supported MCX commodities.</summary>
    public static bool IsCommodity(string? underlying) =>
        ForUnderlying(underlying) == MarketSegment.Commodity;

    /// <summary>
    /// Resolves the segment from a broker tradingsymbol (e.g. "CRUDEOIL25JAN6000CE").
    /// <para>
    /// Used where only the tradingsymbol is available - notably order validation, where
    /// the signal navigation property may not have been loaded. Commodity tradingsymbols
    /// always begin with the underlying name, so a prefix match is sufficient and cannot
    /// collide with the equity indices.
    /// </para>
    /// </summary>
    public static MarketSegment ForTradingSymbol(string? tradingSymbol)
    {
        if (string.IsNullOrWhiteSpace(tradingSymbol))
            return MarketSegment.Equity;

        var symbol = tradingSymbol.Trim();
        foreach (var _ in from commodity in CommodityUnderlyings
                          where symbol.StartsWith(commodity, StringComparison.OrdinalIgnoreCase)
                          select new { })
        {
            return MarketSegment.Commodity;
        }

        return MarketSegment.Equity;
    }
}
