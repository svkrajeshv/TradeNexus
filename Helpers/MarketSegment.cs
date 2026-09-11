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
    /// <para>
    /// The "mini" contracts (GOLDM, SILVERM, SILVERMIC, CRUDEOILM, NATGASMINI) are
    /// <b>separate instruments</b> with their own lot size, strike ladder and premium
    /// range - not aliases of the full-size contract. They must be listed here in
    /// their own right, otherwise a "GOLDM" signal degrades to a prefix match on
    /// "GOLD" and books the wrong (far larger) series.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> CommodityUnderlyings =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Full-size contracts
            "CRUDEOIL", "NATURALGAS", "GOLD", "SILVER",
            // Mini / micro contracts - distinct instruments, not aliases
            "CRUDEOILM", "NATGASMINI", "GOLDM", "SILVERM", "SILVERMIC"
        };

    /// <summary>
    /// Channel shorthand mapped to the exchange's tradingsymbol root.
    /// <para>
    /// Telegram channels quote MCX contracts loosely - "NATGAS" for NATURALGAS,
    /// "GOLD MINI" for GOLDM - while the instrument master only ever carries the
    /// exchange name. Aliases are resolved before segment/lot/expiry lookup so both
    /// spellings converge on one canonical underlying.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> UnderlyingAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NATGAS"] = "NATURALGAS",
            ["NATURALGASMINI"] = "NATGASMINI",
            ["NATGASMINI"] = "NATGASMINI",
            ["GOLDMINI"] = "GOLDM",
            ["GOLDMIN"] = "GOLDM",
            ["CRUDEOILMINI"] = "CRUDEOILM",
            ["SILVERMINI"] = "SILVERM",
            ["SILVERMICRO"] = "SILVERMIC"
        };

    /// <summary>
    /// Resolves channel shorthand to the exchange's underlying name. Returns the input
    /// (trimmed and upper-cased) unchanged when it is not an alias, so equity indices
    /// and already-canonical commodities pass straight through.
    /// </summary>
    public static string NormalizeUnderlying(string? underlying)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return string.Empty;

        var trimmed = underlying.Trim().ToUpperInvariant();
        return UnderlyingAliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    /// <summary>
    /// Every spelling a signal may use for a commodity - the canonical names plus the
    /// channel aliases. Used to build the parser's underlying pattern so shorthand is
    /// recognised at parse time rather than failing the whole signal.
    /// </summary>
    public static IEnumerable<string> CommodityUnderlyingTokens =>
        CommodityUnderlyings.Concat(UnderlyingAliases.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the segment for an underlying, defaulting to
    /// <see cref="MarketSegment.Equity"/> for anything unrecognised.
    /// </summary>
    public static MarketSegment ForUnderlying(string? underlying) =>
        !string.IsNullOrWhiteSpace(underlying) && CommodityUnderlyings.Contains(NormalizeUnderlying(underlying))
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
    public static MarketSegment ForTradingSymbol(string? tradingSymbol) =>
        string.Equals(ExchangeForTradingSymbol(tradingSymbol), "MCX", StringComparison.OrdinalIgnoreCase)
            ? MarketSegment.Commodity
            : MarketSegment.Equity;

    /// <summary>BSE index options, which route to the BFO segment rather than NFO.</summary>
    private static readonly IReadOnlySet<string> BseUnderlyings =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SENSEX", "BANKEX" };

    /// <summary>
    /// Commodity underlyings ordered longest-first so a mini contract ("GOLDM") is
    /// never claimed by its full-size prefix ("GOLD") during a prefix match.
    /// </summary>
    private static readonly string[] CommodityPrefixes =
        [.. CommodityUnderlyings.OrderByDescending(c => c.Length)];

    /// <summary>True for the BSE indices whose options expire on a Thursday and route to BFO.</summary>
    public static bool IsBseUnderlying(string? underlying) =>
        BseUnderlyings.Contains(NormalizeUnderlying(underlying));

    /// <summary>
    /// The exchange segment an <b>underlying</b> routes to: MCX for commodities,
    /// BFO for BSE indices, NFO otherwise.
    /// <para>
    /// Single source of truth for exchange routing. Every order, quote, WebSocket
    /// subscription and instrument search must derive its segment from here rather
    /// than defaulting to "NFO": a commodity sent on NFO is silently matched against
    /// an unrelated NSE scrip, which the broker then reports as segment NSECMD.
    /// </para>
    /// </summary>
    public static string ExchangeForUnderlying(string? underlying)
    {
        var normalized = NormalizeUnderlying(underlying);
        if (CommodityUnderlyings.Contains(normalized)) return "MCX";
        if (BseUnderlyings.Contains(normalized)) return "BFO";
        return "NFO";
    }

    /// <summary>
    /// The exchange segment for a broker tradingsymbol (e.g. "CRUDEOIL17SEP268750CE"
    /// -> MCX). Tradingsymbols always begin with the underlying, so the prefix decides
    /// the routing. Falls back to NFO for anything unrecognised, preserving the
    /// previous behaviour for equity contracts.
    /// </summary>
    public static string ExchangeForTradingSymbol(string? tradingSymbol)
    {
        if (string.IsNullOrWhiteSpace(tradingSymbol))
            return "NFO";

        var symbol = tradingSymbol.Trim();

        foreach (var commodity in CommodityPrefixes)
        {
            if (symbol.StartsWith(commodity, StringComparison.OrdinalIgnoreCase))
                return "MCX";
        }

        foreach (var bse in BseUnderlyings)
        {
            if (symbol.StartsWith(bse, StringComparison.OrdinalIgnoreCase))
                return "BFO";
        }

        return "NFO";
    }
}
