using Microsoft.Extensions.Logging;
using NexusApp.Interfaces;
using NexusApp.Models;
using System.Text.Json;
using System.Globalization;
using System.Collections.Concurrent;

namespace NexusApp.Brokers.AngelOne;

/// <summary>
/// Angel One broker implementation
/// </summary>
public class AngelOneBroker : IBroker
{
    private readonly AngelOneApiClient _apiClient;
    private readonly ILogger<AngelOneBroker> _logger;
    private readonly AngelInstrumentMaster? _instrumentMaster;
    private bool _isConnected;
    private readonly ConcurrentDictionary<string, CachedInstrument> _instrumentCache = new(StringComparer.OrdinalIgnoreCase);

    public string BrokerName => "AngelOne";
    
    public bool IsConnected => _isConnected && _apiClient.IsAuthenticated;

    public AngelOneBroker(AngelOneApiClient apiClient, ILogger<AngelOneBroker> logger, AngelInstrumentMaster? instrumentMaster = null)
    {
        _apiClient = apiClient;
        _logger = logger;
        _instrumentMaster = instrumentMaster;
        _isConnected = false;
    }

    public async Task<bool> AuthenticateAsync(string clientId, string password, string? twoFactorCode = null)
    {
        if (string.IsNullOrEmpty(twoFactorCode))
        {
            _logger.LogWarning("Two-factor code is required for Angel One authentication");
            return false;
        }

        var result = await _apiClient.LoginAsync(clientId, password, twoFactorCode);
        _isConnected = result;
        return result;
    }

    public async Task<bool> LogoutAsync()
    {
        // Angel One doesn't typically require explicit logout
        _isConnected = false;
        _logger.LogInformation("Logged out from Angel One");
        return await Task.FromResult(true);
    }

    public async Task<bool> RefreshTokenAsync()
    {
        return await _apiClient.RefreshTokenAsync();
    }

    public async Task<BrokerAccountInfo?> GetAccountInfoAsync()
    {
        try
        {
            var profile = await _apiClient.GetProfileAsync();
            if (profile == null)
                return null;

            // Parse Angel One response
            var accountInfo = new BrokerAccountInfo
            {
                LastUpdated = DateTime.UtcNow
            };

            // Note: Actual implementation depends on Angel One API response structure
            // This is a template that needs to be adjusted based on actual API
            
            _logger.LogInformation("Retrieved account info from Angel One");
            return accountInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting account information");
            return null;
        }
    }

    public async Task<BrokerInstrument?> SearchInstrumentAsync(string symbol)
    {
        try
        {
            // Angel One SearchScrip: exchange + searchscrip
            var result = await _apiClient.SearchScripAsync("NFO", symbol);
            if (result == null)
                return null;

            var instrument = new BrokerInstrument
            {
                Symbol          = symbol,
                ExchangeSegment = "NFO"
            };

            // Extract symboltoken from search response (handles array/object/nested payload variants).
            if (result.Value.TryGetProperty("data", out var data))
            {
                var bestScore = -1;
                foreach (var item in EnumerateObjects(data))
                {
                    var candidateToken = TryGetStringAny(item, "symboltoken", "symbolToken", "token");
                    var candidateSymbol = TryGetStringAny(item, "tradingsymbol", "tradingSymbol");
                    var lotSizeRaw = TryGetStringAny(item, "lotsize", "lotSize");
                    if (string.IsNullOrWhiteSpace(candidateToken) && string.IsNullOrWhiteSpace(candidateSymbol))
                        continue;

                    var resolvedSymbol = string.IsNullOrWhiteSpace(candidateSymbol) ? symbol : candidateSymbol!;
                    var exch = TryGetStringAny(item, "exchange", "exch_seg", "exchSeg");
                    var score = 0;
                    if (!string.IsNullOrWhiteSpace(candidateToken))
                        score += 3;
                    if (resolvedSymbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                        score += 5;
                    if (!string.IsNullOrWhiteSpace(exch))
                        score += 1;

                    if (score <= bestScore)
                        continue;

                    bestScore = score;
                    instrument.SymbolToken = candidateToken;
                    instrument.Symbol = resolvedSymbol;
                    if (!string.IsNullOrWhiteSpace(exch))
                        instrument.ExchangeSegment = exch!;
                    if (!string.IsNullOrWhiteSpace(instrument.SymbolToken) &&
                        int.TryParse(instrument.SymbolToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var token))
                        instrument.Token = token;
                    if (!string.IsNullOrWhiteSpace(lotSizeRaw) &&
                        decimal.TryParse(lotSizeRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var lotSize) &&
                        lotSize > 0)
                        instrument.LotSize = lotSize;
                }

                // Cache the lookup so order placement can reuse symbol/token even if subsequent searches fail (403 etc.)
                if (!string.IsNullOrWhiteSpace(instrument.SymbolToken))
                {
                    var cached = new CachedInstrument(
                        instrument.Symbol,
                        instrument.SymbolToken!,
                        string.IsNullOrWhiteSpace(instrument.ExchangeSegment) ? "NFO" : instrument.ExchangeSegment);
                    _instrumentCache[instrument.Symbol] = cached;
                    _instrumentCache[symbol] = cached;
                    _instrumentCache[NormalizeKey(instrument.Symbol)] = cached;
                    _instrumentCache[NormalizeKey(symbol)] = cached;
                }
            }

            if (string.IsNullOrWhiteSpace(instrument.SymbolToken))
            {
                _logger.LogWarning("SearchScrip returned without symbol token for {Symbol}", symbol);
                return null;
            }

            _logger.LogInformation("Found instrument: {Symbol} token-present={HasToken}", instrument.Symbol, !string.IsNullOrWhiteSpace(instrument.SymbolToken));
            return instrument;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching instrument: {Symbol}", symbol);
            return null;
        }
    }

    public async Task<decimal> GetLiveQuoteAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return 0m;

        try
        {
            // 1. Prefer the instrument master — deterministic and doesn't depend on the
            //    flaky /searchScrip endpoint.
            string? token = null;
            string? tradingSymbol = null;
            string? exchange = null;

            if (_instrumentMaster is not null)
            {
                try
                {
                    await _instrumentMaster.EnsureLoadedAsync();
                    var entry = _instrumentMaster.FindByTradingSymbol(symbol);
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Token))
                    {
                        token = entry.Token;
                        tradingSymbol = entry.TradingSymbol;
                        exchange = string.IsNullOrWhiteSpace(entry.ExchangeSegment) ? "NFO" : entry.ExchangeSegment;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Master lookup failed for LTP of {Symbol}", symbol);
                }
            }

            // 2. Fall back to in-memory searchScrip cache (populated during order flow).
            if (string.IsNullOrWhiteSpace(token) &&
                _instrumentCache.TryGetValue(symbol, out var cached))
            {
                token = cached.SymbolToken;
                tradingSymbol = cached.TradingSymbol;
                exchange = cached.Exchange;
            }

            // 3. Last resort: hit /searchScrip once (may 403 — swallow silently).
            if (string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    var instrument = await SearchInstrumentAsync(symbol);
                    if (instrument is not null && !string.IsNullOrWhiteSpace(instrument.SymbolToken))
                    {
                        token = instrument.SymbolToken;
                        tradingSymbol = instrument.Symbol;
                        exchange = InferExchange(instrument.Symbol);
                    }
                }
                catch { /* ignore */ }
            }

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(tradingSymbol))
                return 0m;

            var result = await _apiClient.GetLtpAsync(exchange ?? "NFO", tradingSymbol!, token!);
            if (result is null)
                return 0m;

            // Angel LTP response: { "status": true, "data": { "ltp": 123.45, ... } }
            if (result.Value.TryGetProperty("data", out var data) && TryExtractDecimal(data, out var ltp))
                return ltp;

            return 0m;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting live quote for {Symbol}", symbol);
            return 0m;
        }
    }

    public async Task<BrokerOrderResponse> PlaceOrderAsync(BrokerOrderRequest request)
    {
        try
        {
            var exchange = string.IsNullOrWhiteSpace(request.Exchange) ? InferExchange(request.Symbol) : request.Exchange!;
            var symbolToken = request.SymbolToken ?? string.Empty;
            var tradingSymbol = request.Symbol;

            if (string.IsNullOrWhiteSpace(symbolToken) &&
                _instrumentCache.TryGetValue(request.Symbol, out var cached))
            {
                symbolToken = cached.SymbolToken;
                tradingSymbol = cached.TradingSymbol;
                exchange = cached.Exchange;
            }

            if (string.IsNullOrWhiteSpace(symbolToken) &&
                _instrumentCache.TryGetValue(NormalizeKey(request.Symbol), out var normalizedCached))
            {
                symbolToken = normalizedCached.SymbolToken;
                tradingSymbol = normalizedCached.TradingSymbol;
                exchange = normalizedCached.Exchange;
            }

            // Instrument-master fallback: reliable local lookup by tradingsymbol,
            // then structured (underlying+strike+CE/PE) lookup which handles nearest
            // future expiry when the exact expiry in the tradingsymbol isn't listed.
            if (string.IsNullOrWhiteSpace(symbolToken) && _instrumentMaster is not null)
            {
                try
                {
                    await _instrumentMaster.EnsureLoadedAsync();
                    var entry = _instrumentMaster.FindByTradingSymbol(request.Symbol);

                    if (entry is null)
                    {
                        if (TryDecomposeOptionSymbol(request.Symbol, out var underlying, out var expiry, out var strike, out var optType))
                        {
                            _logger.LogInformation(
                                "Exact tradingsymbol {Symbol} not in master; attempting structured lookup underlying={U} expiry={E:yyyy-MM-dd} strike={S} type={T}",
                                request.Symbol, underlying, expiry, strike, optType);

                            entry = _instrumentMaster.FindOption(underlying, expiry, strike, optType);
                            if (entry is not null)
                            {
                                _logger.LogInformation(
                                    "Structured lookup resolved: {Requested} → {Resolved} (expiry={Expiry})",
                                    request.Symbol, entry.TradingSymbol, entry.Expiry);
                            }
                            else
                            {
                                // Nearest-strike fallback: the exact strike isn't listed
                                // (e.g. NIFTY 27550 when only 27500/27600 exist). Snap to
                                // the closest available strike within ±200 pts.
                                var nearest = _instrumentMaster.FindNearestOption(underlying, expiry, strike, optType, tolerance: 200m);
                                if (nearest is not null)
                                {
                                    _logger.LogWarning(
                                        "Requested strike {Strike} {Type} not listed for {Underlying}; snapping to nearest available strike {SnappedStrike} → {Resolved} (expiry={Expiry})",
                                        strike, optType, underlying, nearest.Strike, nearest.TradingSymbol, nearest.Expiry);
                                    entry = nearest;
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "No contract for {Underlying} {Strike} {Type} within ±200 pts across any expiry — strike not listed by exchange",
                                        underlying, strike, optType);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Could not decompose tradingsymbol {Symbol} for structured lookup", request.Symbol);
                        }
                    }

                    if (entry is not null)
                    {
                        symbolToken = entry.Token;
                        tradingSymbol = entry.TradingSymbol;
                        if (!string.IsNullOrWhiteSpace(entry.ExchangeSegment))
                            exchange = entry.ExchangeSegment;
                        _logger.LogInformation("Resolved symbol token from instrument master: {Symbol} → token={Token}", tradingSymbol, symbolToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Instrument-master fallback failed for {Symbol}", request.Symbol);
                }
            }

            if (string.IsNullOrWhiteSpace(symbolToken))
            {
                var reason = $"Contract {request.Symbol} not found in Angel instrument master. " +
                             "The requested strike may not be listed by the exchange (far-OTM strikes are only added " +
                             "as the underlying moves near them). Verify the strike from the source signal.";
                _logger.LogWarning("Missing symbol token for {Symbol}; {Reason}", request.Symbol, reason);
                return new BrokerOrderResponse
                {
                    Success = false,
                    ErrorMessage = reason
                };
            }

            var orderRequest = new
            {
                variety = "NORMAL",
                tradingsymbol = tradingSymbol,
                symboltoken = symbolToken,
                transactiontype = request.Side == OrderSide.Buy ? "BUY" : "SELL",
                exchange,
                ordertype = request.OrderType == OrderType.Limit ? "LIMIT" : "MARKET",
                producttype = request.ProductType == ProductType.Mis ? "INTRADAY" : "CARRYFORWARD",
                duration = "DAY",
                price = request.OrderType == OrderType.Market
                    ? "0"
                    : request.Price.ToString("0.##", CultureInfo.InvariantCulture),
                quantity = Math.Max(1, (int)Math.Round(request.Quantity, MidpointRounding.AwayFromZero))
            };

            var result = await _apiClient.PlaceOrderAsync(orderRequest);
             
            if (result == null)
                return new BrokerOrderResponse { Success = false, ErrorMessage = "API call failed" };

            if (result.Value.TryGetProperty("status", out var statusEl) &&
                statusEl.ValueKind == JsonValueKind.False)
            {
                var reason = ExtractBrokerError(result.Value);
                _logger.LogWarning("Broker placeOrder rejected: {Reason}", reason);
                return new BrokerOrderResponse
                {
                    Success = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(reason) ? "Order rejected by broker" : reason,
                    Timestamp = DateTime.UtcNow
                };
            }

            var response = new BrokerOrderResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow
            };

            if (result.Value.TryGetProperty("data", out var data))
            {
                switch (data.ValueKind)
                {
                    case JsonValueKind.Object:
                        response.OrderId =
                            TryGetStringAny(data, "orderid", "orderId", "amoOrderId", "exchangeOrderId")
                            ?? string.Empty;
                        break;
                    case JsonValueKind.String:
                        // Some Angel One responses return order id directly as string in "data".
                        response.OrderId = data.GetString() ?? string.Empty;
                        break;
                    case JsonValueKind.Array:
                        // Defensive parse for variant payloads; take first order id if present.
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object)
                            {
                                var arrOrderId = TryGetStringAny(item, "orderid", "orderId", "amoOrderId", "exchangeOrderId");
                                if (string.IsNullOrWhiteSpace(arrOrderId))
                                    continue;
                                response.OrderId = arrOrderId;
                                break;
                            }
                        }
                        break;
                }
            }
            else if (result.Value.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                // Keep success true for HTTP 200, but expose broker message for traceability.
                response.ErrorMessage = msg.GetString();
            }

            _logger.LogInformation("Order placed successfully: {OrderId}", response.OrderId);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order");
            return new BrokerOrderResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<bool> ModifyOrderAsync(string orderId, BrokerOrderRequest request)
    {
        try
        {
            var modifyRequest = new
            {
                orderid = orderId,
                quantity = request.Quantity,
                price = request.Price,
                ordertype = request.OrderType == OrderType.Limit ? "LIMIT" : "MARKET"
            };

            var result = await _apiClient.ModifyOrderAsync(modifyRequest);
            _logger.LogInformation("Order modified: {OrderId}", orderId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error modifying order: {OrderId}", orderId);
            return false;
        }
    }

    private static bool TryExtractDecimal(JsonElement element, out decimal value)
    {
        value = 0m;

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDecimal(out value);

            case JsonValueKind.String:
                return decimal.TryParse(element.GetString(), out value);

            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("ltp") ||
                        prop.NameEquals("lastPrice") ||
                        prop.NameEquals("lastprice") ||
                        prop.NameEquals("last_traded_price") ||
                        prop.NameEquals("close"))
                    {
                        if (TryExtractDecimal(prop.Value, out value))
                            return true;
                    }
                }

                foreach (var prop in element.EnumerateObject())
                {
                    if (TryExtractDecimal(prop.Value, out value))
                        return true;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryExtractDecimal(item, out value))
                        return true;
                }
                break;
        }

        return false;
    }

    private static string InferExchange(string symbol)
    {
        var s = (symbol ?? string.Empty).ToUpperInvariant();
        return s.Contains("SENSEX") || s.Contains("BANKEX") ? "BFO" : "NFO";
    }

    private static readonly string[] KnownUnderlyings =
        { "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX", "BANKEX", "NIFTY" };

    /// <summary>
    /// Decomposes an Angel option tradingsymbol into its parts. Handles both
    /// weekly (DDMMMYY) and monthly (YYMMM) formats, e.g.
    ///   NIFTY14JUL2627250PE    → NIFTY, 2026-07-14, 27250, PE
    ///   BANKNIFTY26JUL57000PE  → BANKNIFTY, 2026-07-{last-Thu}, 57000, PE  (expiry guessed to today)
    /// The expiry only needs to be an approximate anchor; <c>FindOption</c> falls
    /// back to the nearest future expiry with matching underlying+strike+type.
    /// </summary>
    private static bool TryDecomposeOptionSymbol(
        string tradingSymbol,
        out string underlying,
        out DateTime expiry,
        out decimal strike,
        out string optionType)
    {
        underlying = string.Empty;
        expiry = DateTime.Today;
        strike = 0m;
        optionType = string.Empty;

        if (string.IsNullOrWhiteSpace(tradingSymbol))
            return false;

        var s = tradingSymbol.Trim().ToUpperInvariant();

        // 1. option type (last 2 chars)
        if (s.Length < 8) return false;
        var opt = s[^2..];
        if (opt != "CE" && opt != "PE") return false;
        optionType = opt;
        var body = s[..^2];

        // 2. underlying (longest known prefix)
        foreach (var u in KnownUnderlyings)
        {
            if (body.StartsWith(u, StringComparison.Ordinal))
            {
                underlying = u;
                break;
            }
        }
        if (string.IsNullOrEmpty(underlying)) return false;

        var mid = body[underlying.Length..];
        if (mid.Length < 3) return false;

        // 3. Try weekly format: DDMMMYY[STRIKE]  e.g. 14JUL2627250
        //    or monthly:       YYMMM[STRIKE]     e.g. 26JUL57000
        //    Both patterns have MMM at a known offset — locate the 3-letter month.
        int monthIdx = -1;
        for (var i = 0; i + 3 <= mid.Length; i++)
        {
            var candidate = mid.Substring(i, 3);
            if (IsMonth(candidate))
            {
                monthIdx = i;
                break;
            }
        }
        if (monthIdx < 0) return false;

        var monthStr = mid.Substring(monthIdx, 3);
        var month = MonthNumber(monthStr);
        if (month == 0) return false;

        int day = 1;
        int year;
        int strikeStart;

        if (monthIdx == 2)
        {
            // weekly: DD MMM YY … (14 JUL 26 27250)
            if (!int.TryParse(mid[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out day))
                return false;
            if (mid.Length < 7 ||
                !int.TryParse(mid.Substring(5, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out year))
                return false;
            strikeStart = 7;
        }
        else if (monthIdx == 0)
        {
            // rare "MMM …" leading — not expected for Angel
            return false;
        }
        else
        {
            // monthly: YY MMM (26 JUL 57000). YY is the substring before month.
            if (!int.TryParse(mid[..monthIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out year))
                return false;
            strikeStart = monthIdx + 3;
        }

        if (strikeStart >= mid.Length) return false;
        var strikePart = mid[strikeStart..];
        if (!decimal.TryParse(strikePart, NumberStyles.Number, CultureInfo.InvariantCulture, out strike) || strike <= 0)
            return false;

        // Angel master year is 2-digit; expand.
        var fullYear = year < 100 ? 2000 + year : year;
        try
        {
            expiry = new DateTime(fullYear, month, Math.Clamp(day, 1, DateTime.DaysInMonth(fullYear, month)));
        }
        catch
        {
            expiry = DateTime.Today;
        }

        return true;
    }

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4,
        ["MAY"] = 5, ["JUN"] = 6, ["JUL"] = 7, ["AUG"] = 8,
        ["SEP"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DEC"] = 12
    };
    private static bool IsMonth(string s) => Months.ContainsKey(s);
    private static int MonthNumber(string s) => Months.TryGetValue(s, out var m) ? m : 0;

    private static string? TryGetStringAny(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in item.EnumerateObject())
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (!prop.Name.Equals(names[i], StringComparison.OrdinalIgnoreCase))
                    continue;

                return prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number when prop.Value.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
                    _ => null
                };
            }
        }

        return null;
    }

    private static string NormalizeKey(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var prop in element.EnumerateObject())
            {
                foreach (var nested in EnumerateObjects(prop.Value))
                    yield return nested;
            }
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateObjects(item))
                    yield return nested;
            }
        }
    }

    private static string ExtractBrokerError(JsonElement root)
    {
        if (root.TryGetProperty("errorMessage", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? string.Empty;
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var emsg))
                return emsg.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            return msg.GetString() ?? string.Empty;

        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.String)
                return data.GetString() ?? string.Empty;
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("message", out var dmsg) && dmsg.ValueKind == JsonValueKind.String)
                    return dmsg.GetString() ?? string.Empty;
                if (data.TryGetProperty("error", out var derr) && derr.ValueKind == JsonValueKind.String)
                    return derr.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private readonly record struct CachedInstrument(string TradingSymbol, string SymbolToken, string Exchange);

    public async Task<bool> CancelOrderAsync(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            _logger.LogWarning("CancelOrderAsync called with empty orderId");
            return false;
        }

        try
        {
            // Angel requires BOTH orderid and variety. Try to look up the current
            // variety+status from the order book; skip cancel if already terminal.
            string variety = "NORMAL";
            try
            {
                var book = await _apiClient.GetOrderBookAsync();
                if (book is not null && book.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in data.EnumerateArray())
                    {
                        var rowId = TryGetStringAny(row, "orderid", "orderId");
                        if (!string.Equals(rowId, orderId, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var v = TryGetStringAny(row, "variety");
                        if (!string.IsNullOrWhiteSpace(v)) variety = v!.ToUpperInvariant();

                        var status = (TryGetStringAny(row, "status", "orderstatus") ?? string.Empty).ToLowerInvariant();
                        // Angel statuses that cannot be cancelled
                        if (status.Contains("complete") || status.Contains("filled") ||
                            status.Contains("rejected") || status.Contains("cancel"))
                        {
                            _logger.LogInformation(
                                "Order {OrderId} not cancellable — current status '{Status}'",
                                orderId, status);
                            return false;
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read order book before cancel — proceeding with defaults");
            }

            var cancelRequest = new { variety, orderid = orderId };
            var (ok, message) = await _apiClient.CancelOrderAsync(cancelRequest);
            if (ok)
                _logger.LogInformation("Order cancelled: {OrderId} (variety={Variety})", orderId, variety);
            else
                _logger.LogWarning("Broker refused cancel for {OrderId} (variety={Variety}): {Message}",
                    orderId, variety, message);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling order: {OrderId}", orderId);
            return false;
        }
    }

    public async Task<List<BrokerOrder>> GetOrderBookAsync()
    {
        try
        {
            var result = await _apiClient.GetOrderBookAsync();
            var orders = new List<BrokerOrder>();

            if (result != null && result.Value.TryGetProperty("data", out var data))
            {
                foreach (var item in EnumerateObjects(data))
                {
                    var orderId = TryGetStringAny(item, "orderId", "orderid", "amoOrderId", "exchangeOrderId");
                    var symbol = TryGetStringAny(item, "tradingSymbol", "tradingsymbol");
                    if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(symbol))
                        continue;

                    var order = new BrokerOrder
                    {
                        OrderId = orderId,
                        Symbol = symbol,
                        Quantity = ParseDecimal(TryGetStringAny(item, "quantity")),
                        Price = ParseDecimal(TryGetStringAny(item, "price")),
                        Side = ParseOrderSide(TryGetStringAny(item, "transactionType", "transactiontype")),
                        Status = ParseOrderStatus(
                            TryGetStringAny(item, "orderStatus", "orderstatus", "status"),
                            TryGetStringAny(item, "rejectionReason", "text", "statusCode")),
                        FilledQuantity = ParseNullableDecimal(TryGetStringAny(item, "filledShares", "filledshares")),
                        AveragePrice = ParseNullableDecimal(TryGetStringAny(item, "averagePrice", "averageprice")),
                        Text = TryGetStringAny(item, "text", "rejectionReason", "statusMessage", "statusmessage"),
                        CreatedAt = ParseDateTime(
                            TryGetStringAny(item, "updateTime", "updatetime", "orderValidityDate"),
                            DateTime.UtcNow)
                    };

                    orders.Add(order);
                }
            }

            _logger.LogInformation("Retrieved {Count} orders from order book", orders.Count);

            return orders;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting order book");
            return new List<BrokerOrder>();
        }
    }

    public async Task<List<BrokerPosition>> GetPositionBookAsync()
    {
        try
        {
            var result = await _apiClient.GetPositionBookAsync();
            var positions = new List<BrokerPosition>();

            if (result != null && result.Value.TryGetProperty("data", out var data))
            {
                // Parse positions from Angel One response
                _logger.LogInformation("Retrieved {Count} positions from position book", positions.Count);
            }

            return positions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting position book");
            return new List<BrokerPosition>();
        }
    }

    public async Task<List<BrokerTrade>> GetTradeBookAsync()
    {
        try
        {
            var result = await _apiClient.GetTradeBookAsync();
            var trades = new List<BrokerTrade>();

            if (result != null && result.Value.TryGetProperty("data", out var data))
            {
                // Parse trades from Angel One response
                _logger.LogInformation("Retrieved {Count} trades from trade book", trades.Count);
            }

            return trades;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trade book");
            return new List<BrokerTrade>();
        }
    }

    private static decimal ParseDecimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;

    private static decimal? ParseNullableDecimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static DateTime ParseDateTime(string? raw, DateTime fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return parsed.ToUniversalTime();

        return fallback;
    }

    private static OrderSide ParseOrderSide(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return value is "B" or "BUY" ? OrderSide.Buy : OrderSide.Sell;
    }

    private static OrderStatus ParseOrderStatus(string? status, string? rejectionText)
    {
        var value = (status ?? string.Empty).Trim().ToLowerInvariant();
        var hasRejectText = !string.IsNullOrWhiteSpace(rejectionText);

        if (value.Contains("rejected") || value.Contains("reject") || hasRejectText)
            return OrderStatus.Rejected;
        if (value.Contains("cancel"))
            return OrderStatus.Cancelled;
        if (value.Contains("complete") || value.Contains("filled") || value.Contains("executed"))
            return OrderStatus.Executed;
        if (value.Contains("open") || value.Contains("pending") || value.Contains("trigger"))
            return OrderStatus.Accepted;
        if (value.Contains("fail"))
            return OrderStatus.Failed;

        return OrderStatus.Pending;
    }
}
