using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NexusApp.Brokers.AngelOne;

/// <summary>
/// Loads and caches Angel One's official instrument master (OpenAPIScripMaster.json).
///
/// Per Angel One docs, the recommended way to resolve symboltoken for options
/// is to download this daily-refreshed file rather than depending on the
/// unreliable /searchScrip endpoint (which frequently returns 403 or empty results).
///
/// Source: https://smartapi.angelbroking.com/docs/Instruments
/// File:   https://margincalculator.angelbroking.com/OpenAPI_File/files/OpenAPIScripMaster.json
///
/// Master entry format (each element in root array):
///   { "token": "61867",
///     "symbol": "BANKNIFTY14JUL2657000PE",   // tradingsymbol
///     "name":   "BANKNIFTY",                  // underlying name
///     "expiry": "14JUL2026",                  // DDMMMYYYY
///     "strike": "5700000",                    // strike * 100
///     "lotsize": "30",
///     "instrumenttype": "OPTIDX",             // OPTIDX / FUTIDX / OPTSTK / ...
///     "exch_seg": "NFO",                      // NFO / BFO / NSE / BSE
///     "tick_size": "5.000000" }
/// </summary>
public sealed class AngelInstrumentMaster
{
    private const string MasterUrl =
        "https://margincalculator.angelbroking.com/OpenAPI_File/files/OpenAPIScripMaster.json";

    private readonly HttpClient _httpClient;
    private readonly ILogger<AngelInstrumentMaster> _logger;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private ConcurrentDictionary<string, MasterEntry> _byTradingSymbol =
        new(StringComparer.OrdinalIgnoreCase);
    private List<MasterEntry> _optionIndex = new();
    private DateTime _loadedAtUtc;

    public AngelInstrumentMaster(
        IHttpClientFactory httpFactory,
        ILogger<AngelInstrumentMaster> logger)
    {
        _httpClient = httpFactory.CreateClient("AngelOne");
        _logger = logger;
        var dir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "angel-instrument-master.json");
    }

    public bool IsLoaded => _byTradingSymbol.Count > 0;
    public int Count => _byTradingSymbol.Count;
    public DateTime LoadedAtUtc => _loadedAtUtc;

    /// <summary>
    /// Clears the in-memory index and forces the next <see cref="EnsureLoadedAsync"/>
    /// call to re-download from Angel. Used by the daily maintenance job.
    /// </summary>
    public void ResetLoadState()
    {
        _byTradingSymbol = new(StringComparer.OrdinalIgnoreCase);
        _optionIndex = new();
        _loadedAtUtc = default;
    }

    /// <summary>
    /// Ensures the master file is loaded. If cached copy on disk is present and less than
    /// 24 hours old, uses it. Otherwise downloads a fresh copy and rewrites the cache.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        // Load at most once per calendar day (IST). If already loaded and the
        // last load happened on today's date, nothing to do.
        if (IsLoaded && _loadedAtUtc.ToLocalTime().Date == DateTime.Today)
            return;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (IsLoaded && _loadedAtUtc.ToLocalTime().Date == DateTime.Today)
                return;

            // Cache is considered fresh if it was written on today's local date.
            var cacheFresh = File.Exists(_cachePath)
                && File.GetLastWriteTime(_cachePath).Date == DateTime.Today;

            string? json = null;
            if (cacheFresh)
            {
                try
                {
                    json = await File.ReadAllTextAsync(_cachePath, ct);
                    _logger.LogInformation("Loaded Angel instrument master from disk cache ({Path})", _cachePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read cached instrument master; will re-download");
                    json = null;
                }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogInformation("Downloading Angel instrument master from {Url}", MasterUrl);
                json = await _httpClient.GetStringAsync(MasterUrl, ct);
                try
                {
                    await File.WriteAllTextAsync(_cachePath, json, ct);
                    _logger.LogInformation("Cached Angel instrument master to {Path} ({Bytes} bytes)", _cachePath, json.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not persist instrument master cache");
                }
            }

            LoadFromJson(json!);
            _loadedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Angel instrument master");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Underlyings we actually trade. Non-listed index/stock options are dropped
    /// from the in-memory index to save memory (~90% reduction).
    /// </summary>
    private static readonly HashSet<string> SupportedUnderlyings = new(StringComparer.OrdinalIgnoreCase)
    {
        "NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX", "BANKEX"
    };

    /// <summary>Number of strikes (per index / per expiry / per CE|PE side) to retain.</summary>
    private const int StrikesPerSide = 100;   // → 200 per (index, expiry) counting CE+PE

    /// <summary>Expiries beyond this many days are dropped — we don't trade far-dated series.</summary>
    private const int MaxExpiryDays = 60;

    private void LoadFromJson(string json)
    {
        var byTs = new ConcurrentDictionary<string, MasterEntry>(StringComparer.OrdinalIgnoreCase);

        // Two buckets:
        //   * optionsSupported: OPTIDX for our supported underlyings within MaxExpiryDays — kept & pruned by strike window.
        //   * nonOptions:       everything else (FUT, EQ, etc.) — kept as-is for tradingsymbol lookups but not indexed.
        var optionsSupported = new List<MasterEntry>(capacity: 20_000);
        var today = DateTime.Today;
        var maxExpiryDate = today.AddDays(MaxExpiryDays);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Instrument master root is not a JSON array (kind={Kind})", doc.RootElement.ValueKind);
            return;
        }

        var totalParsed = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var entry = MasterEntry.From(el);
            if (entry is null)
                continue;
            totalParsed++;

            if (IsOption(entry.InstrumentType))
            {
                // Drop unsupported underlyings and far-dated expiries entirely.
                if (!SupportedUnderlyings.Contains(entry.Name))
                    continue;
                if (entry.ExpiryDate == default ||
                    entry.ExpiryDate < today ||
                    entry.ExpiryDate > maxExpiryDate)
                    continue;

                optionsSupported.Add(entry);
                // Don't insert into byTs yet — we'll do it after strike-window pruning
                // so only the retained contracts consume memory.
            }
            else
            {
                // Non-option (FUT / EQ / CDS / MCX) — keep for tradingsymbol lookups.
                byTs[entry.TradingSymbol] = entry;
            }
        }

        // Prune options to the N strikes closest to ATM per (underlying, expiry, side).
        // ATM is approximated as the median listed strike (option chains are ~symmetric
        // around spot as long as the exchange lists strikes both sides of it).
        var pruned = PruneToNearestStrikes(optionsSupported);

        foreach (var entry in pruned)
            byTs[entry.TradingSymbol] = entry;

        _byTradingSymbol = byTs;
        _optionIndex = pruned;

        _logger.LogInformation(
            "Instrument master loaded: parsed={Parsed} kept={Kept} (options={Options}, non-options={NonOpt}). Dropped {Dropped} contracts to save memory.",
            totalParsed, byTs.Count, pruned.Count, byTs.Count - pruned.Count, totalParsed - byTs.Count);
    }

    private static List<MasterEntry> PruneToNearestStrikes(List<MasterEntry> options)
    {
        // Group by underlying + expiry + side (CE/PE)
        var groups = options.GroupBy(e => new
        {
            e.Name,
            e.Expiry,
            Side = e.TradingSymbol.EndsWith("CE", StringComparison.OrdinalIgnoreCase) ? "CE" : "PE"
        });

        var retained = new List<MasterEntry>(capacity: options.Count / 4);

        foreach (var g in groups)
        {
            var sorted = g.OrderBy(x => x.StrikeScaled).ToList();
            if (sorted.Count <= StrikesPerSide)
            {
                retained.AddRange(sorted);
                continue;
            }

            // ATM ≈ median strike of what the exchange has listed.
            var atm = sorted[sorted.Count / 2].StrikeScaled;

            // Keep the N strikes closest to ATM.
            var nearest = sorted
                .OrderBy(x => Math.Abs(x.StrikeScaled - atm))
                .Take(StrikesPerSide);

            retained.AddRange(nearest);
        }

        return retained;
    }

    private static bool IsOption(string instrumentType)
        => instrumentType.Equals("OPTIDX", StringComparison.OrdinalIgnoreCase)
        || instrumentType.Equals("OPTSTK", StringComparison.OrdinalIgnoreCase)
        || instrumentType.Equals("OPTFUT", StringComparison.OrdinalIgnoreCase)
        || instrumentType.Equals("OPTCUR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Exact tradingsymbol lookup (fastest path).
    /// </summary>
    public MasterEntry? FindByTradingSymbol(string tradingSymbol)
    {
        if (string.IsNullOrWhiteSpace(tradingSymbol))
            return null;
        return _byTradingSymbol.TryGetValue(tradingSymbol, out var e) ? e : null;
    }

    /// <summary>
    /// Structured lookup: match by underlying name + expiry date + strike + CE/PE.
    /// This is the reliable way to resolve options — searchScrip is not needed.
    /// </summary>
    public MasterEntry? FindOption(
        string underlying,
        DateTime expiry,
        decimal strike,
        string optionType)
    {
        if (_optionIndex.Count == 0)
            return null;

        var name = (underlying ?? string.Empty).Trim().ToUpperInvariant();
        var opt = (optionType ?? string.Empty).Trim().ToUpperInvariant();
        if (opt != "CE" && opt != "PE")
            return null;

        // Angel stores strike as (strike * 100) rounded — e.g. 57000 → 5700000.
        var strikeScaled = (long)Math.Round(strike * 100m, MidpointRounding.AwayFromZero);
        var expiryUpper = FormatExpiry(expiry);

        MasterEntry? exactMatch = null;
        MasterEntry? nameOnlyFallback = null;

        for (var i = 0; i < _optionIndex.Count; i++)
        {
            var e = _optionIndex[i];
            if (!e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!e.TradingSymbol.EndsWith(opt, StringComparison.OrdinalIgnoreCase))
                continue;
            if (e.StrikeScaled != strikeScaled)
                continue;

            if (e.Expiry.Equals(expiryUpper, StringComparison.OrdinalIgnoreCase))
            {
                exactMatch = e;
                break;
            }

            // Fallback: same underlying+strike+type, closest future expiry
            if (nameOnlyFallback is null && e.ExpiryDate >= DateTime.Today)
                nameOnlyFallback = e;
            else if (nameOnlyFallback is not null
                     && e.ExpiryDate >= DateTime.Today
                     && e.ExpiryDate < nameOnlyFallback.ExpiryDate)
                nameOnlyFallback = e;
        }

        return exactMatch ?? nameOnlyFallback;
    }

    /// <summary>
    /// Nearest-strike lookup: for the same underlying+expiry+type, returns the
    /// contract whose strike is closest to <paramref name="strike"/> within
    /// <paramref name="tolerance"/> points. Falls back across expiries (nearest
    /// future first) when the requested expiry isn't available.
    /// Useful when a signal quotes a strike that isn't listed (e.g., far-OTM
    /// gaps like NIFTY 27550 when only 27500/27600 exist, or 27000/28000).
    /// </summary>
    public MasterEntry? FindNearestOption(
        string underlying,
        DateTime expiry,
        decimal strike,
        string optionType,
        decimal tolerance = 200m)
    {
        if (_optionIndex.Count == 0)
            return null;

        var name = (underlying ?? string.Empty).Trim().ToUpperInvariant();
        var opt = (optionType ?? string.Empty).Trim().ToUpperInvariant();
        if (opt != "CE" && opt != "PE")
            return null;

        var toleranceScaled = (long)Math.Round(tolerance * 100m);
        var wantScaled = (long)Math.Round(strike * 100m);
        var expiryUpper = FormatExpiry(expiry);

        MasterEntry? bestSameExpiry = null;
        long bestSameExpiryDelta = long.MaxValue;
        MasterEntry? bestOtherExpiry = null;
        long bestOtherExpiryScore = long.MaxValue; // strike-delta weighted by days

        for (var i = 0; i < _optionIndex.Count; i++)
        {
            var e = _optionIndex[i];
            if (!e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!e.TradingSymbol.EndsWith(opt, StringComparison.OrdinalIgnoreCase))
                continue;
            if (e.ExpiryDate < DateTime.Today)
                continue;

            var delta = Math.Abs(e.StrikeScaled - wantScaled);
            if (delta > toleranceScaled)
                continue;

            if (e.Expiry.Equals(expiryUpper, StringComparison.OrdinalIgnoreCase))
            {
                if (delta < bestSameExpiryDelta)
                {
                    bestSameExpiryDelta = delta;
                    bestSameExpiry = e;
                }
            }
            else
            {
                // Prefer closer strike, break ties by nearer expiry.
                var daysAhead = (e.ExpiryDate - DateTime.Today).Days;
                var score = delta * 1000 + Math.Max(0, daysAhead);
                if (score < bestOtherExpiryScore)
                {
                    bestOtherExpiryScore = score;
                    bestOtherExpiry = e;
                }
            }
        }

        return bestSameExpiry ?? bestOtherExpiry;
    }

    private static string FormatExpiry(DateTime expiry)
    {
        // Angel master expiry format: DDMMMYYYY, e.g. "14JUL2026"
        return expiry.ToString("ddMMMyyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
    }

    public sealed class MasterEntry
    {
        public required string Token { get; init; }
        public required string TradingSymbol { get; init; }
        public required string Name { get; init; }
        public required string Expiry { get; init; }
        public DateTime ExpiryDate { get; init; }
        public long StrikeScaled { get; init; }
        public decimal Strike { get; init; }
        public decimal LotSize { get; init; }
        public string InstrumentType { get; init; } = string.Empty;
        public string ExchangeSegment { get; init; } = string.Empty;
        public decimal TickSize { get; init; }

        internal static MasterEntry? From(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object)
                return null;

            var token = GetString(el, "token");
            var tradingSymbol = GetString(el, "symbol");
            var name = GetString(el, "name");
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(tradingSymbol))
                return null;

            var expiry = GetString(el, "expiry") ?? string.Empty;
            _ = DateTime.TryParseExact(
                expiry,
                "ddMMMyyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var expiryDate);

            var strikeRaw = GetString(el, "strike") ?? "0";
            _ = decimal.TryParse(strikeRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var strikeScaledDec);
            var strikeScaled = (long)Math.Round(strikeScaledDec, MidpointRounding.AwayFromZero);
            var strike = strikeScaledDec / 100m;

            var lotSizeRaw = GetString(el, "lotsize") ?? "0";
            _ = decimal.TryParse(lotSizeRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var lotSize);

            var tickRaw = GetString(el, "tick_size") ?? "0";
            _ = decimal.TryParse(tickRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var tick);

            return new MasterEntry
            {
                Token = token!,
                TradingSymbol = tradingSymbol!,
                Name = (name ?? string.Empty).ToUpperInvariant(),
                Expiry = expiry.ToUpperInvariant(),
                ExpiryDate = expiryDate,
                StrikeScaled = strikeScaled,
                Strike = strike,
                LotSize = lotSize,
                InstrumentType = GetString(el, "instrumenttype") ?? string.Empty,
                ExchangeSegment = GetString(el, "exch_seg") ?? string.Empty,
                TickSize = tick
            };
        }

        private static string? GetString(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v))
                return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number when v.TryGetInt64(out var n)
                    => n.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.Number => v.GetDouble().ToString(CultureInfo.InvariantCulture),
                _ => null
            };
        }
    }
}
