using System.Collections.Concurrent;
using System.Globalization;

namespace NexusApp.Brokers.AliceBlue;

/// <summary>
/// Loads and caches AliceBlue's per-exchange contract master CSV, used to resolve
/// the numeric instrument token (<c>symbol_id</c>) required by the ANT placeOrder API.
///
/// AliceBlue publishes daily-refreshed CSVs per exchange:
///   https://v2api.aliceblueonline.com/restpy/static/contract_master/NFO.csv
///   https://v2api.aliceblueonline.com/restpy/static/contract_master/BFO.csv
///
/// The header row includes columns such as:
///   Exch, Exchange Segment, Symbol, Token, Instrument Type, Option Type,
///   Strike Price, Trading Symbol, Formatted Ins Name, Expiry Date, Lot Size,
///   Tick Size, ...
///
/// We index primarily by the "Trading Symbol" column (the value the app builds
/// via SymbolBuilder), falling back to "Symbol".
/// </summary>
public sealed class AliceBlueContractMaster
{
    private const string BaseUrl = "https://v2api.aliceblueonline.com/restpy/static/contract_master";

    // Exchanges we resolve contracts for (FnO index + stock options).
    private static readonly string[] Exchanges = { "NFO", "BFO" };

    private readonly HttpClient _httpClient;
    private readonly ILogger<AliceBlueContractMaster> _logger;
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private ConcurrentDictionary<string, ContractEntry> _byTradingSymbol =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedAtUtc;

    public AliceBlueContractMaster(IHttpClientFactory httpFactory, ILogger<AliceBlueContractMaster> logger)
    {
        _httpClient = httpFactory.CreateClient("AliceBlue");
        _logger = logger;
        _cacheDir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(_cacheDir);
    }

    public bool IsLoaded => _byTradingSymbol.Count > 0;
    public int Count => _byTradingSymbol.Count;
    public DateTime LoadedAtUtc => _loadedAtUtc;

    /// <summary>
    /// Clears the in-memory index so the next <see cref="EnsureLoadedAsync"/> re-downloads.
    /// </summary>
    public void ResetLoadState()
    {
        _byTradingSymbol = new(StringComparer.OrdinalIgnoreCase);
        _loadedAtUtc = default;
    }

    /// <summary>
    /// Ensures the contract master is loaded (at most once per calendar day).
    /// Uses a disk cache written on the current local date when available.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (IsLoaded && _loadedAtUtc.ToLocalTime().Date == DateTime.Today)
            return;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (IsLoaded && _loadedAtUtc.ToLocalTime().Date == DateTime.Today)
                return;

            var byTs = new ConcurrentDictionary<string, ContractEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var exchange in Exchanges)
            {
                var csv = await LoadCsvAsync(exchange, ct);
                if (string.IsNullOrWhiteSpace(csv))
                    continue;

                ParseInto(byTs, csv, exchange);
            }

            if (byTs.Count > 0)
            {
                _byTradingSymbol = byTs;
                _loadedAtUtc = DateTime.UtcNow;
                _logger.LogInformation("AliceBlue contract master loaded: {Count} contracts", byTs.Count);
            }
            else
            {
                _logger.LogWarning("AliceBlue contract master load produced no contracts");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load AliceBlue contract master");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Exact trading-symbol lookup.</summary>
    public ContractEntry? FindByTradingSymbol(string tradingSymbol)
    {
        if (string.IsNullOrWhiteSpace(tradingSymbol))
            return null;
        return _byTradingSymbol.TryGetValue(tradingSymbol.Trim(), out var e) ? e : null;
    }

    private async Task<string?> LoadCsvAsync(string exchange, CancellationToken ct)
    {
        var cachePath = Path.Combine(_cacheDir, $"aliceblue-contract-{exchange}.csv");
        var cacheFresh = File.Exists(cachePath) && File.GetLastWriteTime(cachePath).Date == DateTime.Today;

        if (cacheFresh)
        {
            try
            {
                _logger.LogInformation("Loaded AliceBlue {Exchange} contract master from disk cache", exchange);
                return await File.ReadAllTextAsync(cachePath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read cached AliceBlue {Exchange} contract master; re-downloading", exchange);
            }
        }

        try
        {
            var url = $"{BaseUrl}/{exchange}.csv";
            _logger.LogInformation("Downloading AliceBlue contract master from {Url}", url);
            var csv = await _httpClient.GetStringAsync(url, ct);
            try { await File.WriteAllTextAsync(cachePath, csv, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not persist AliceBlue {Exchange} contract cache", exchange); }
            return csv;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not download AliceBlue {Exchange} contract master", exchange);
            return null;
        }
    }

    private void ParseInto(ConcurrentDictionary<string, ContractEntry> byTs, string csv, string exchange)
    {
        using var reader = new StringReader(csv);
        var header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header))
            return;

        var cols = SplitCsvLine(header);
        int Idx(params string[] names)
        {
            for (var i = 0; i < cols.Length; i++)
                foreach (var n in names)
                    if (cols[i].Trim().Equals(n, StringComparison.OrdinalIgnoreCase))
                        return i;
            return -1;
        }

        var tokenIdx = Idx("Token");
        var tradingSymIdx = Idx("Trading Symbol", "TradingSymbol");
        var symbolIdx = Idx("Symbol");
        var lotIdx = Idx("Lot Size", "LotSize");
        var tickIdx = Idx("Tick Size", "TickSize");
        var exchIdx = Idx("Exch", "Exchange");

        if (tokenIdx < 0 || (tradingSymIdx < 0 && symbolIdx < 0))
        {
            _logger.LogWarning("AliceBlue {Exchange} contract master missing required columns", exchange);
            return;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = SplitCsvLine(line);
            if (fields.Length <= tokenIdx)
                continue;

            var token = Get(fields, tokenIdx);
            var tradingSymbol = tradingSymIdx >= 0 ? Get(fields, tradingSymIdx) : null;
            var symbol = symbolIdx >= 0 ? Get(fields, symbolIdx) : null;
            var key = !string.IsNullOrWhiteSpace(tradingSymbol) ? tradingSymbol : symbol;

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(key))
                continue;

            var entry = new ContractEntry
            {
                Token = token!,
                TradingSymbol = key!,
                Exchange = exchIdx >= 0 ? (Get(fields, exchIdx) ?? exchange) : exchange,
                LotSize = ParseDecimal(lotIdx >= 0 ? Get(fields, lotIdx) : null),
                TickSize = ParseDecimal(tickIdx >= 0 ? Get(fields, tickIdx) : null)
            };

            byTs[key!] = entry;
            if (!string.IsNullOrWhiteSpace(symbol) && !byTs.ContainsKey(symbol!))
                byTs[symbol!] = entry;
        }
    }

    private static string Get(string[] fields, int idx)
        => idx >= 0 && idx < fields.Length ? fields[idx].Trim() : string.Empty;

    private static decimal ParseDecimal(string? value)
        => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static string[] SplitCsvLine(string line)
    {
        // Contract master files are plain comma-separated without embedded quotes/commas.
        return line.Split(',');
    }

    public sealed class ContractEntry
    {
        public required string Token { get; init; }
        public required string TradingSymbol { get; init; }
        public string Exchange { get; init; } = "NFO";
        public decimal LotSize { get; init; }
        public decimal TickSize { get; init; }
    }
}
