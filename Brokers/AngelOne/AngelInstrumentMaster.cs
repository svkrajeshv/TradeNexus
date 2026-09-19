using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NexusApp.Helpers;

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
    /// <summary>
    /// Generous timeout for the one-a-day master download (~25 MB). Deliberately longer
    /// than the shared "AngelOne" client timeout, which is tuned for small API calls.
    /// </summary>
    private static readonly TimeSpan MasterDownloadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Number of download attempts before falling back to the cached copy.</summary>
    private const int MasterDownloadAttempts = 3;

    /// <summary>
    /// Dedicated client for the large master download. Its <see cref="HttpClient.Timeout"/> is
    /// disabled (infinite) so the per-call <see cref="MasterDownloadTimeout"/> linked token is the
    /// only timeout authority. The shared "AngelOne" client keeps its short 30s timeout, which would
    /// otherwise abort this ~25 MB download mid-stream regardless of the linked token.
    /// </summary>
    private readonly HttpClient _downloadClient;

    private readonly ILogger<AngelInstrumentMaster> _logger;
    private readonly string _masterUrl;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private ConcurrentDictionary<string, MasterEntry> _byTradingSymbol =
        new(StringComparer.OrdinalIgnoreCase);
    private List<MasterEntry> _optionIndex = [];
    private DateTime _loadedAtUtc;

    public AngelInstrumentMaster(
        IHttpClientFactory httpFactory,
        ILogger<AngelInstrumentMaster> logger,
        IConfiguration configuration)
    {
        _downloadClient = httpFactory.CreateClient("AngelOne");
        // Disable the client-level timeout for the master download; the linked
        // CancellationToken (MasterDownloadTimeout) governs the deadline instead.
        _downloadClient.Timeout = Timeout.InfiniteTimeSpan;
        _logger = logger;
        _masterUrl = configuration["AngelOne:InstrumentMasterUrl"]
            ?? throw new InvalidOperationException("AngelOne:InstrumentMasterUrl is not configured.");
        var dir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "angel-instrument-master.json");
    }

    public bool IsLoaded => !_byTradingSymbol.IsEmpty;
    public int Count => _byTradingSymbol.Count;
    public DateTime LoadedAtUtc => _loadedAtUtc;

    /// <summary>
    /// True when the master is loaded in-memory <b>and</b> that load happened on
    /// today's (IST) date. Used to decide whether a login-time refresh is needed.
    /// </summary>
    public bool IsLoadedToday => IsLoaded && _loadedAtUtc != default && _loadedAtUtc.ToIst().Date == DateTimeExtensions.IstToday();

    /// <summary>
    /// Clears the in-memory index and forces the next <see cref="EnsureLoadedAsync"/>
    /// call to re-download from Angel. Used by the daily maintenance job.
    /// </summary>
    public void ResetLoadState()
    {
        _byTradingSymbol = new(StringComparer.OrdinalIgnoreCase);
        _optionIndex = [];
        _loadedAtUtc = default;
    }

    /// <summary>
    /// Forces a clean, once-per-day refresh: if the in-memory index was not loaded
    /// today, the on-disk cache is deleted and a fresh copy is downloaded and
    /// re-indexed. If it was already loaded today, this is a no-op. Safe to call on
    /// every login click — it only does real work once per calendar day. Returns the
    /// number of contracts loaded (0 if the download/parse failed).
    /// </summary>
    public async Task<int> RefreshDailyAsync(CancellationToken ct = default)
    {
        if (IsLoadedToday)
            return Count;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (IsLoadedToday)
                return Count;

            // Drop stale in-memory index and delete the disk cache so EnsureLoadedAsync
            // is guaranteed to re-download a fresh copy rather than reuse yesterday's file.
            _byTradingSymbol = new(StringComparer.OrdinalIgnoreCase);
            _optionIndex = [];
            _loadedAtUtc = default;

            try
            {
                if (File.Exists(_cachePath))
                {
                    File.Delete(_cachePath);
                    _logger.LogInformation("Deleted stale instrument master cache for daily refresh ({Path})", _cachePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete instrument master cache; will re-download anyway");
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        await EnsureLoadedAsync(ct);
        return Count;
    }


    /// <summary>
    /// Ensures the master file is loaded. If cached copy on disk is present and less than
    /// 24 hours old, uses it. Otherwise downloads a fresh copy and rewrites the cache.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        // Load at most once per calendar day (IST). If already loaded and the
        // last load happened on today's date, nothing to do.
        if (IsLoadedToday)
            return;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (IsLoadedToday)
                return;

            // Cache is considered fresh if it was written on today's IST date.
            var cacheFresh = File.Exists(_cachePath)
                && File.GetLastWriteTimeUtc(_cachePath).ToIst().Date == DateTimeExtensions.IstToday();

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
                _logger.LogInformation("Downloading Angel instrument master from {Url}", _masterUrl);
                try
                {
                    // The master is a large (~25 MB) file. The shared "AngelOne" client
                    // uses a short (30s) timeout tuned for normal API calls, which aborts
                    // this download mid-stream — so nothing ever gets cached to reuse.
                    // Use a dedicated client with no client-level timeout and a generous
                    // per-call timeout via a linked token so a complete file lands on disk
                    // once per day and can be reused thereafter. Retry a few times to ride
                    // out transient network blips before falling back to the cache.
                    for (var attempt = 1; attempt <= MasterDownloadAttempts; attempt++)
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(MasterDownloadTimeout);
                        try
                        {
                            json = await DownloadMasterJsonAsync(timeoutCts.Token);
                            break;
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested && attempt < MasterDownloadAttempts)
                        {
                            var delay = TimeSpan.FromSeconds(2 * attempt);
                            _logger.LogWarning(ex,
                                "Instrument master download attempt {Attempt}/{Max} failed; retrying in {Delay}s",
                                attempt, MasterDownloadAttempts, delay.TotalSeconds);
                            await Task.Delay(delay, ct);
                        }
                    }

                    // Trim the ~36 MB payload down to only the underlyings we actually
                    // trade before caching/parsing. The endpoint has no server-side filter,
                    // so we filter the downloaded JSON here: the cached file and every
                    // subsequent reparse shrink from ~36 MB to a few hundred KB.
                    if (string.IsNullOrWhiteSpace(json))
                        throw new InvalidOperationException("Angel instrument master download returned no data.");

                    json = FilterToSupportedUnderlyings(json);

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
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Today's download failed (timeout, network blip, Angel outage). Fall
                    // back to the last good cached file — even if it is from a previous day —
                    // so the app has a usable instrument master instead of an empty index.
                    json = TryReadLastGoodCache();
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _logger.LogError(ex, "Failed to download Angel instrument master and no cached copy is available");
                        return;
                    }
                    _logger.LogWarning(ex, "Download failed; falling back to last cached instrument master ({Path})", _cachePath);
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
    /// Downloads the complete Angel instrument master in one response.
    /// A failed response is discarded in full; the caller retries a new download from byte zero.
    /// </summary>
    private async Task<string> DownloadMasterJsonAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _masterUrl);
        using var response = await _downloadClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 81920,
            leaveOpen: false);

        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Reads the last successfully-persisted master file from disk, regardless of its age.
    /// Used as a resilience fallback when today's download fails so the app can still
    /// resolve symbols using yesterday's contracts rather than an empty index.
    /// Returns null if no cached file exists or it cannot be read.
    /// </summary>
    private string? TryReadLastGoodCache()
    {
        try
        {
            if (File.Exists(_cachePath))
                return File.ReadAllText(_cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read fallback instrument master cache ({Path})", _cachePath);
        }
        return null;
    }

    /// <summary>
    /// True when a master row may be indexed for the given underlying.
    /// <para>
    /// Angel's master lists the <b>same</b> commodity names on more than one segment:
    /// MCX carries the real commodity contracts, while <c>NCO</c> (NSE Commodity, shown
    /// as NSECMD in the order book) and <c>NCDEX</c> carry look-alike rows with identical
    /// <c>name</c> values but different tokens, expiries and lot sizes. For example
    /// CRUDEOIL 8700 PE exists as both:
    /// </para>
    /// <code>
    /// CRUDEOIL17SEP268700PE  token=576535  MCX  expiry=17SEP2026  lotsize=100
    /// CRUDEOIL26SEP8700PE    token=135733  NCO  expiry=10SEP2026  lotsize=1
    /// </code>
    /// <para>
    /// Every lookup here matches on <c>name</c> alone, so without this guard the NCO row
    /// wins whenever its expiry is nearer — which is what booked commodity orders on the
    /// NSECMD segment with a lot size of 1. Commodities are therefore accepted only from
    /// MCX; equity underlyings are unaffected.
    /// </para>
    /// </summary>
    private static bool IsAllowedSegment(string name, string exchangeSegment) =>
        !MarketSegments.IsCommodity(name) ||
        exchangeSegment.Equals("MCX", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Underlyings we actually trade. Non-listed index/stock options are dropped
    /// from the in-memory index to save memory (~90% reduction).
    /// <para>
    /// The MCX commodities are loaded unconditionally rather than being gated on the
    /// <c>Mcx.Enabled</c> setting: loading them is harmless, and gating would mean
    /// flipping that toggle required a full master re-download before commodity
    /// symbols could resolve. Keeping the load unconditional makes the toggle take
    /// effect immediately, at the cost of a modestly larger index while MCX is off.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> SupportedUnderlyings = new(StringComparer.OrdinalIgnoreCase)
    {
        // NSE / BSE index options
        "NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX", "BANKEX",
        // MCX commodity options (mini/micro contracts are separate instruments)
        "CRUDEOIL", "NATURALGAS", "GOLD", "SILVER",
        "CRUDEOILM", "NATGASMINI", "GOLDM", "SILVERM", "SILVERMIC"
    };

    /// <summary>
    /// Number of strikes (per index / per expiry / per CE|PE side) to retain around ATM.
    /// Directional Telegram signals are frequently far-OTM, so this window must be wide
    /// enough that a legitimate signal strike is never pruned away — otherwise the exact
    /// expiry match fails and the fallback silently rolls the order onto a farther/monthly
    /// contract. 100 strikes/side ≈ ±2,400 pts (NIFTY) / ±4,800 pts (BANKNIFTY), which
    /// comfortably covers realistic signal strikes while staying memory-bounded (index
    /// underlyings only, ≤ MaxExpiryDays).
    /// </summary>
    private const int StrikesPerSide = 100;  // → 200 per (index, expiry) counting CE+PE

    /// <summary>Expiries beyond this many days are dropped — we don't trade far-dated series.</summary>
    private const int MaxExpiryDays = 60;

    /// <summary>
    /// Filters the raw instrument-master JSON array down to only rows for the
    /// <see cref="SupportedUnderlyings"/> we trade. The Angel endpoint cannot filter
    /// server-side, so we do it here immediately after download: the cached file and
    /// every subsequent parse shrink from ~36 MB (~100k rows) to a few hundred KB.
    /// On any error the original JSON is returned unchanged so behaviour is never worse
    /// than before.
    /// </summary>
    private string FilterToSupportedUnderlyings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return json;

            var buffer = new ArrayBufferWriter<byte>(1 << 20);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                var kept = 0;
                var total = 0;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    total++;
                    if (el.ValueKind != JsonValueKind.Object)
                        continue;

                    var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(name) || !SupportedUnderlyings.Contains(name))
                        continue;

                    // Commodity names are duplicated on NCO/NCDEX with different tokens and
                    // lot sizes; keep only the MCX rows so those look-alikes can never be
                    // resolved by a name-based lookup.
                    var seg = el.TryGetProperty("exch_seg", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString() ?? string.Empty
                        : string.Empty;
                    if (!IsAllowedSegment(name!, seg))
                        continue;

                    el.WriteTo(writer);
                    kept++;
                }
                writer.WriteEndArray();

                _logger.LogInformation(
                    "Filtered instrument master to supported underlyings: kept {Kept} of {Total} rows",
                    kept, total);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pre-filter instrument master; caching full payload instead");
            return json;
        }
    }

    private void LoadFromJson(string json)
    {
        var byTs = new ConcurrentDictionary<string, MasterEntry>(StringComparer.OrdinalIgnoreCase);

        // Two buckets:
        //   * optionsSupported: OPTIDX for our supported underlyings within MaxExpiryDays — kept & pruned by strike window.
        //   * nonOptions:       everything else (FUT, EQ, etc.) — kept as-is for tradingsymbol lookups but not indexed.
        var optionsSupported = new List<MasterEntry>(capacity: 20_000);
        var today = DateTimeExtensions.IstToday();
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
                // A commodity must come from MCX: the NCO/NCDEX look-alikes carry the same
                // name but a different token, expiry and lot size. This also protects a
                // cache written before the download filter above existed.
                if (!IsAllowedSegment(entry.Name, entry.ExchangeSegment))
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
                // Non-option (FUT / EQ / CDS / MCX). We only trade the supported index
                // options, so retain non-options ONLY for those same underlyings
                // (e.g. index FUTIDX / index spot used for CMP/LTP lookups). Everything
                // else — the ~28k equities, stock F&O, currency and commodity rows — is
                // dropped to save memory and speed up load/indexing.
                if (!SupportedUnderlyings.Contains(entry.Name))
                    continue;
                if (!IsAllowedSegment(entry.Name, entry.ExchangeSegment))
                    continue;

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
            "Instrument master loaded: parsed={Parsed} kept={Kept} (options={Options}, mcx={NonOpt}). Dropped {Dropped} contracts to save memory.",
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

            // MCX commodity chains are small (a few hundred contracts in total) and the
            // signals routinely quote deep-OTM strikes. Pruning to a median-centred ATM
            // window drops those strikes from the NEAREST expiry while leaving them listed
            // on a farther one, which makes FindOption's cross-expiry fallback silently
            // roll the order onto the wrong series. Retain commodities in full.
            if (MarketSegments.IsCommodity(g.Key.Name))
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
    /// Returns the nearest listed (future) expiry date for the given underlying
    /// straight from the loaded instrument master. This is the authoritative
    /// source of truth — it already reflects weekly/monthly cadence, exchange
    /// weekday rules and holiday shifts, so callers should prefer this over any
    /// computed weekday heuristic. Returns <c>null</c> if the master isn't loaded
    /// or the underlying has no listed contracts.
    /// </summary>
    /// <param name="underlying">Index/underlying name, e.g. NIFTY, SENSEX, BANKNIFTY.</param>
    /// <param name="from">Only expiries on/after this date are considered (defaults to today).</param>
    public DateTime? NearestExpiry(string underlying, DateTime? from = null)
    {
        if (_optionIndex.Count == 0)
            return null;

        var name = (underlying ?? string.Empty).Trim().ToUpperInvariant();
        if (name.Length == 0)
            return null;

        var floor = (from ?? DateTimeExtensions.IstToday()).Date;

        DateTime? nearest = null;
        for (var i = 0; i < _optionIndex.Count; i++)
        {
            var e = _optionIndex[i];
            if (!e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (e.ExpiryDate == default || e.ExpiryDate.Date < floor)
                continue;
            if (nearest is null || e.ExpiryDate.Date < nearest.Value)
                nearest = e.ExpiryDate.Date;
        }

        return nearest;
    }

    /// <summary>
    /// Returns the listed expiry for the given underlying that falls in the requested
    /// month, or <c>null</c> when that month has no listed contract. Used for monthly
    /// contracts (MCX commodities) where the signal names only the month ("EXPIRY - SEP")
    /// and the exact expiry day varies per commodity.
    /// </summary>
    public DateTime? ExpiryInMonth(string underlying, int year, int month)
    {
        if (_optionIndex.Count == 0)
            return null;

        var name = (underlying ?? string.Empty).Trim().ToUpperInvariant();
        if (name.Length == 0 || month is < 1 or > 12)
            return null;

        DateTime? earliest = null;
        for (var i = 0; i < _optionIndex.Count; i++)
        {
            var e = _optionIndex[i];
            if (!e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (e.ExpiryDate == default || e.ExpiryDate.Year != year || e.ExpiryDate.Month != month)
                continue;
            if (earliest is null || e.ExpiryDate.Date < earliest.Value)
                earliest = e.ExpiryDate.Date;
        }

        return earliest;
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
        var istToday = DateTimeExtensions.IstToday();

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
            if (nameOnlyFallback is null && e.ExpiryDate >= istToday)
                nameOnlyFallback = e;
            else if (nameOnlyFallback is not null
                     && e.ExpiryDate >= istToday
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
        var istToday = DateTimeExtensions.IstToday();

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
            if (e.ExpiryDate < istToday)
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
                var daysAhead = (e.ExpiryDate - istToday).Days;
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
