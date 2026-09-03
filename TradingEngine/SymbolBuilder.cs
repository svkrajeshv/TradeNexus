using System.Globalization;
using Microsoft.Extensions.Logging;
using NexusApp.Brokers.AngelOne;
using NexusApp.Helpers;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.TradingEngine;

/// <summary>
/// Builds broker-ready option symbols from parsed index/strike/type/expiry data.
/// Uses the broker's instrument search when available and falls back to the
/// standard NSE/BSE symbol formats so that unit tests and offline development
/// still produce a usable symbol string.
/// </summary>
public sealed class SymbolBuilder(IBroker broker, ILogger<SymbolBuilder> logger, AngelInstrumentMaster? instrumentMaster = null)
{
    private static readonly HashSet<string> NseIndices = new(StringComparer.OrdinalIgnoreCase)
    {
        "NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY"
    };

    private static readonly HashSet<string> BseIndices = new(StringComparer.OrdinalIgnoreCase)
    {
        "SENSEX", "BANKEX"
    };

    private static readonly string[] MonthAbbrev =
    [
        "JAN","FEB","MAR","APR","MAY","JUN","JUL","AUG","SEP","OCT","NOV","DEC"
    ];

    private readonly IBroker _broker = broker;
    private readonly AngelInstrumentMaster? _instrumentMaster = instrumentMaster;
    private readonly ILogger<SymbolBuilder> _logger = logger;

    /// <summary>
    /// Returns the tradable symbol and lot size for the given signal.
    /// If <paramref name="expiryOverride"/> is null, the nearest weekly expiry is used.
    ///
    /// Resolution order:
    /// 1. Angel instrument master (local, reliable) — matches by index+expiry+strike+CE/PE.
    /// 2. Angel searchScrip via IBroker (unreliable; used only if master miss).
    /// 3. Standard symbol fallback (offline / test scenarios).
    /// </summary>
    public async Task<ResolvedSymbol> ResolveAsync(ParsedSignal signal, DateTime? expiryOverride = null)
    {
        var expiry = expiryOverride ?? await ResolveNearestExpiryAsync(signal.Index);
        var candidates = BuildCandidates(signal.Index, signal.Strike, signal.OptionType, expiry);
        var optionType = signal.OptionType == OptionType.Ce ? "CE" : "PE";

        // 1) Local instrument master (preferred).
        if (_instrumentMaster is not null)
        {
            try
            {
                await _instrumentMaster.EnsureLoadedAsync();

                // 1a) Structured lookup FIRST — this matches the *exact* resolved
                // expiry date (weekly vs monthly). It must take precedence over the
                // fuzzy tradingsymbol candidates below: for BSE indices the string
                // "{INDEX}{yy}{MMM}{strike}{opt}" (e.g. SENSEX26AUG78600PE) is itself
                // a real *monthly* contract, so a plain tradingsymbol hit would
                // silently override the intended weekly expiry.
                var structured = _instrumentMaster.FindOption(signal.Index, expiry, signal.Strike, optionType);
                if (structured is not null)
                {
                    _logger.LogInformation(
                        "Instrument master hit (structured): {Symbol} token={Token} exch={Exch} expiry={Expiry:yyyy-MM-dd}",
                        structured.TradingSymbol, structured.Token, structured.ExchangeSegment, expiry);
                    return ToResolved(structured, expiry, SymbolSource.Broker);
                }

                // 1b) Fuzzy tradingsymbol candidates — fallback only when the
                // structured (expiry-accurate) lookup misses.
                foreach (var candidate in candidates)
                {
                    var byTs = _instrumentMaster.FindByTradingSymbol(candidate);
                    if (byTs is not null)
                    {
                        _logger.LogInformation(
                            "Instrument master hit (tradingsymbol fallback): {Symbol} token={Token} exch={Exch}",
                            byTs.TradingSymbol, byTs.Token, byTs.ExchangeSegment);
                        return ToResolved(byTs, expiry, SymbolSource.Broker);
                    }
                }

                _logger.LogWarning(
                    "Instrument master miss for {Index} {Strike} {OptionType} expiry {Expiry:yyyy-MM-dd}",
                    signal.Index, signal.Strike, optionType, expiry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Instrument master lookup failed; falling back to searchScrip");
            }
        }

        // 2) Broker searchScrip fallback (best-effort; may return null/403).
        try
        {
            if (_broker.IsConnected)
            {
                foreach (var candidate in candidates)
                {
                    var instrument = await _broker.SearchInstrumentAsync(candidate);
                    if (instrument is null || string.IsNullOrWhiteSpace(instrument.Symbol) || string.IsNullOrWhiteSpace(instrument.SymbolToken))
                        continue;

                    return new ResolvedSymbol
                    {
                        Symbol = instrument.Symbol,
                        SymbolToken = instrument.SymbolToken,
                        LotSize = instrument.LotSize > 0 ? instrument.LotSize : DefaultLotSize(signal.Index),
                        Token = instrument.Token,
                        Exchange = instrument.ExchangeSegment,
                        Expiry = expiry,
                        Source = SymbolSource.Broker
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Broker instrument lookup failed for {Index} {Strike} {OptionType}", signal.Index, signal.Strike, signal.OptionType);
        }

        return new ResolvedSymbol
        {
            Symbol = candidates[0],
            LotSize = DefaultLotSize(signal.Index),
            Exchange = ExchangeFor(signal.Index),
            Expiry = expiry,
            Source = SymbolSource.Fallback
        };
    }

    /// <summary>
    /// Resolves the nearest expiry for an index. Prefers the <b>actual</b> nearest
    /// listed expiry from the Angel instrument master (authoritative — reflects
    /// weekly/monthly cadence, exchange weekday rules and holiday shifts). Only
    /// falls back to the computed weekday heuristic when the master is unavailable
    /// (offline / tests / underlying not listed).
    /// </summary>
    private async Task<DateTime> ResolveNearestExpiryAsync(string index)
    {
        if (_instrumentMaster is not null)
        {
            try
            {
                await _instrumentMaster.EnsureLoadedAsync();
                var listed = _instrumentMaster.NearestExpiry(index, DateTimeExtensions.IstToday());
                if (listed is not null)
                {
                    _logger.LogInformation(
                        "Nearest expiry for {Index} resolved from instrument master: {Expiry:yyyy-MM-dd} (dow={Dow})",
                        index, listed.Value, listed.Value.DayOfWeek);
                    return listed.Value;
                }

                _logger.LogWarning(
                    "No listed expiry found in instrument master for {Index}; using computed weekday heuristic.",
                    index);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to resolve nearest expiry from instrument master for {Index}; using computed heuristic.",
                    index);
            }
        }

        return NearestWeeklyExpiry(DateTimeExtensions.IstToday(), index);
    }

    private static ResolvedSymbol ToResolved(AngelInstrumentMaster.MasterEntry entry, DateTime expiry, SymbolSource source)
    {
        _ = int.TryParse(entry.Token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokenInt);
        return new ResolvedSymbol
        {
            Symbol = entry.TradingSymbol,
            SymbolToken = entry.Token,
            LotSize = entry.LotSize > 0 ? entry.LotSize : DefaultLotSize(entry.Name),
            Token = tokenInt,
            Exchange = string.IsNullOrWhiteSpace(entry.ExchangeSegment) ? "NFO" : entry.ExchangeSegment,
            Expiry = entry.ExpiryDate == default ? expiry : entry.ExpiryDate,
            Source = source
        };
    }

    /// <summary>
    /// Builds a standard NSE/BSE-style option symbol: e.g. NIFTY24JUL24250PE
    /// </summary>
    public static string BuildStandardSymbol(string index, decimal strike, OptionType type, DateTime expiry)
    {
        var yy = (expiry.Year % 100).ToString("00", CultureInfo.InvariantCulture);
        var mon = MonthAbbrev[expiry.Month - 1];
        var strikeStr = strike.ToString("0", CultureInfo.InvariantCulture);
        var opt = type == OptionType.Ce ? "CE" : "PE";
        return $"{index.ToUpperInvariant()}{yy}{mon}{strikeStr}{opt}";
    }

    private static List<string> BuildCandidates(string index, decimal strike, OptionType type, DateTime expiry)
    {
        var idx = index.ToUpperInvariant();
        var mon = MonthAbbrev[expiry.Month - 1];
        var yy = (expiry.Year % 100).ToString("00", CultureInfo.InvariantCulture);
        var dd = expiry.Day.ToString("00", CultureInfo.InvariantCulture);
        var strikeStr = strike.ToString("0", CultureInfo.InvariantCulture);
        var opt = type == OptionType.Ce ? "CE" : "PE";

        var candidates = new List<string>(4)
        {
            $"{idx}{dd}{mon}{yy}{strikeStr}{opt}", // e.g. BANKNIFTY28JUL2657000CE
            $"{idx}{yy}{mon}{strikeStr}{opt}",      // e.g. BANKNIFTY26JUL57000PE
            $"{idx}{dd}{mon}{strikeStr}{opt}",      // occasional variant
            BuildStandardSymbol(index, strike, type, expiry)
        };

        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>(candidates.Count);
        foreach (var c in candidates)
        {
            if (dedup.Add(c))
                ordered.Add(c);
        }
        return ordered;
    }

    /// <summary>
    /// Indices that still trade <b>weekly</b> options after the SEBI expiry
    /// rationalisation (effective Sep 2025, in force 2026). Only the benchmark
    /// index of each exchange retains weekly contracts:
    ///   • NSE  → NIFTY   (weekly, expires Tuesday)
    ///   • BSE  → SENSEX  (weekly, expires Thursday)
    /// All other indices (BANKNIFTY, FINNIFTY, MIDCPNIFTY, BANKEX) are
    /// <b>monthly-only</b>.
    /// </summary>
    private static readonly HashSet<string> WeeklyIndices = new(StringComparer.OrdinalIgnoreCase)
    {
        "NIFTY", "SENSEX"
    };

    /// <summary>True when the index still lists weekly option contracts.</summary>
    public static bool HasWeeklyExpiry(string index) => WeeklyIndices.Contains(index);

    /// <summary>
    /// The weekday on which an index's options expire.
    ///   • BSE indices (SENSEX, BANKEX) → Thursday
    ///   • NSE indices (NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY) → Tuesday
    /// </summary>
    public static DayOfWeek ExpiryWeekdayFor(string index) =>
        BseIndices.Contains(index) ? DayOfWeek.Thursday : DayOfWeek.Tuesday;

    /// <summary>
    /// Returns the correct nearest expiry for the index based on the current
    /// (2026) SEBI rules:
    ///   • Weekly indices (NIFTY, SENSEX) → nearest upcoming expiry weekday.
    ///   • Monthly-only indices → last expiry weekday of the month (rolling to
    ///     the next month once the current month's expiry has passed).
    /// NSE indices expire on Tuesday, BSE indices (SENSEX/BANKEX) on Thursday.
    /// </summary>
    public static DateTime NearestWeeklyExpiry(DateTime from, string index)
    {
        var target = ExpiryWeekdayFor(index);

        if (HasWeeklyExpiry(index))
        {
            var d = from.Date;
            while (d.DayOfWeek != target)
                d = d.AddDays(1);
            return d;
        }

        // Monthly-only index: use the last <target> weekday of the current month,
        // rolling into next month if that date has already passed.
        var monthly = LastWeekdayOfMonth(from.Year, from.Month, target);
        if (monthly < from.Date)
        {
            var next = from.Date.AddMonths(1);
            monthly = LastWeekdayOfMonth(next.Year, next.Month, target);
        }
        return monthly;
    }

    /// <summary>Returns the last occurrence of <paramref name="weekday"/> in the given month.</summary>
    private static DateTime LastWeekdayOfMonth(int year, int month, DayOfWeek weekday)
    {
        var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        while (d.DayOfWeek != weekday)
            d = d.AddDays(-1);
        return d;
    }

    /// <summary>
    /// Provides reasonable default lot sizes for common indices. These will be
    /// overridden by broker-supplied data whenever the instrument master is available.
    /// </summary>
    public static decimal DefaultLotSize(string index) => index.ToUpperInvariant() switch
    {
        "NIFTY" => 75,
        "BANKNIFTY" => 30,
        "FINNIFTY" => 65,
        "MIDCPNIFTY" => 120,
        "SENSEX" => 20,
        "BANKEX" => 30,
        _ => 1
    };

    private static string ExchangeFor(string index) =>
        BseIndices.Contains(index) ? "BFO" :
        NseIndices.Contains(index) ? "NFO" : "NFO";
}

public sealed class ResolvedSymbol
{
    public string Symbol { get; set; } = string.Empty;
    public string? SymbolToken { get; set; }
    public decimal LotSize { get; set; }
    public int Token { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    public SymbolSource Source { get; set; }
}

public enum SymbolSource
{
    Broker = 0,
    Fallback = 1
}
