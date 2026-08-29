using NexusApp.Interfaces;
using NexusApp.Models;
using System.Globalization;
using System.Text.Json;

namespace NexusApp.Brokers.AliceBlue;

/// <summary>
/// AliceBlue (ANT v2) broker implementation backed by <see cref="AliceBlueApiClient"/>.
/// </summary>
public sealed class AliceBlueBroker(
    AliceBlueApiClient apiClient,
    ILogger<AliceBlueBroker> logger,
    AliceBlueContractMaster? contractMaster = null) : IBroker
{
    private readonly AliceBlueApiClient _apiClient = apiClient;
    private readonly ILogger<AliceBlueBroker> _logger = logger;
    private readonly AliceBlueContractMaster? _contractMaster = contractMaster;
    private bool _isConnected = false;

    public string BrokerName => "AliceBlue";

    public bool IsConnected => _isConnected && _apiClient.IsAuthenticated;

    public async Task<bool> AuthenticateAsync(string clientId, string password, string? twoFactorCode = null)
    {
        // AliceBlue ANT v2 uses userId + apiKey (no password/TOTP). The API key is
        // supplied via AliceBlueApiClient.Configure() before this call; twoFactorCode
        // carries the apiKey as a fallback when Configure() was not used.
        var result = await _apiClient.LoginAsync(clientId, twoFactorCode ?? string.Empty);
        _isConnected = result;
        return result;
    }

    public Task<bool> LogoutAsync()
    {
        _isConnected = false;
        _logger.LogInformation("Logged out from AliceBlue");
        return Task.FromResult(true);
    }

    public Task<bool> RefreshTokenAsync()
        // AliceBlue sessions are valid for the trading day; no refresh endpoint.
        => Task.FromResult(_apiClient.IsAuthenticated);

    public async Task<BrokerAccountInfo?> GetAccountInfoAsync()
    {
        var info = new BrokerAccountInfo { LastUpdated = DateTime.UtcNow };
        try
        {
            var result = await _apiClient.GetRmsLimitsAsync();
            if (result is null)
                return info;

            // AliceBlue RMS response is typically an array (per-segment) or a single object.
            // Aggregate the cash / margin fields across whatever segments are returned.
            foreach (var item in EnumerateObjects(result.Value))
            {
                info.CashBalance     += ParseDecimal(TryGetStringAny(item, "cashmarginavailable", "cashMarginAvailable", "net"));
                info.AvailableMargin += ParseDecimal(TryGetStringAny(item, "net", "netCashAvailable", "cashmarginavailable"));
                info.UsedMargin      += ParseDecimal(TryGetStringAny(item, "marginused", "marginUsed", "premiumPresent"));
                info.TotalMargin     += ParseDecimal(TryGetStringAny(item, "deposit", "brokerCollateralAmount", "collateralvalue"));
            }

            _logger.LogInformation(
                "Retrieved AliceBlue account info: available={Available} used={Used}",
                info.AvailableMargin, info.UsedMargin);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting AliceBlue account information");
        }
        return info;
    }

    public async Task<BrokerInstrument?> SearchInstrumentAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || _contractMaster is null)
        {
            _logger.LogDebug("AliceBlue SearchInstrumentAsync unavailable for {Symbol}", symbol);
            return null;
        }

        try
        {
            await _contractMaster.EnsureLoadedAsync();
            var entry = _contractMaster.FindByTradingSymbol(symbol);
            if (entry is null)
                return null;

            return new BrokerInstrument
            {
                Symbol = entry.TradingSymbol,
                Name = entry.TradingSymbol,
                SymbolToken = entry.Token,
                LotSize = entry.LotSize,
                TickSize = entry.TickSize,
                ExchangeSegment = entry.Exchange
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AliceBlue instrument lookup failed for {Symbol}", symbol);
            return null;
        }
    }

    /// <summary>
    /// Resolves the numeric instrument token (symbol_id) for a symbol via the
    /// contract master, preferring any token already supplied on the request.
    /// </summary>
    private async Task<(string? Token, string Exchange)> ResolveTokenAsync(string symbol, string? suppliedToken, string exchange)
    {
        if (!string.IsNullOrWhiteSpace(suppliedToken))
            return (suppliedToken, exchange);

        if (_contractMaster is null || string.IsNullOrWhiteSpace(symbol))
            return (null, exchange);

        try
        {
            await _contractMaster.EnsureLoadedAsync();
            var entry = _contractMaster.FindByTradingSymbol(symbol);
            if (entry is not null)
                return (entry.Token, string.IsNullOrWhiteSpace(entry.Exchange) ? exchange : entry.Exchange);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AliceBlue token resolution failed for {Symbol}", symbol);
        }

        return (null, exchange);
    }

    public async Task<decimal> GetLiveQuoteAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return 0m;

        try
        {
            var (token, exchange) = await ResolveTokenAsync(symbol, null, "NFO");
            var result = await _apiClient.GetScripQuoteAsync(exchange, token ?? symbol);
            if (result is null)
                return 0m;

            var ltp = TryGetStringAny(result.Value, "Ltp", "ltp", "lastTradedPrice");
            return ParseDecimal(ltp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting AliceBlue quote for {Symbol}", symbol);
            return 0m;
        }
    }

    public async Task<BrokerOrderResponse> PlaceOrderAsync(BrokerOrderRequest request)
    {
        try
        {
            var isBuy = request.Side == OrderSide.Buy;
            var isRobo = string.Equals(request.Variety, "ROBO", StringComparison.OrdinalIgnoreCase);

            var priceType = request.OrderType == OrderType.Market ? "MKT" : "L";
            var price = request.OrderType == OrderType.Market
                ? "0"
                : request.Price.ToString("0.##", CultureInfo.InvariantCulture);
            var quantity = Math.Max(1, (int)Math.Round(request.Quantity, MidpointRounding.AwayFromZero));
            var exchange = string.IsNullOrWhiteSpace(request.Exchange) ? "NFO" : request.Exchange!;

            // Resolve the numeric instrument token (symbol_id) required by ANT placeOrder.
            var (resolvedToken, resolvedExchange) = await ResolveTokenAsync(request.Symbol, request.SymbolToken, exchange);
            exchange = resolvedExchange;
            if (string.IsNullOrWhiteSpace(resolvedToken))
            {
                var tokenReason = $"Contract {request.Symbol} not found in AliceBlue contract master; " +
                             "cannot resolve instrument token (symbol_id).";
                _logger.LogWarning("AliceBlue order aborted: {Reason}", tokenReason);
                return new BrokerOrderResponse { Success = false, ErrorMessage = tokenReason };
            }

            // AliceBlue expects an array payload for executePlaceOrder.
            object leg = isRobo
                ? new
                {
                    complexty = "BO",
                    discqty = "0",
                    exch = exchange,
                    pCode = "MIS",
                    prctyp = priceType,
                    price,
                    qty = quantity,
                    ret = "DAY",
                    symbol_id = resolvedToken,
                    trading_symbol = request.Symbol,
                    transtype = isBuy ? "BUY" : "SELL",
                    stopLoss = request.StopLossPoints.ToString("0.##", CultureInfo.InvariantCulture),
                    target = request.SquareOffPoints.ToString("0.##", CultureInfo.InvariantCulture),
                    trailing_stop_loss = request.TrailingStopLossPoints > 0
                        ? request.TrailingStopLossPoints.ToString("0.##", CultureInfo.InvariantCulture)
                        : "0"
                }
                : new
                {
                    complexty = "REGULAR",
                    discqty = "0",
                    exch = exchange,
                    pCode = request.ProductType == ProductType.Nrml ? "NRML" : "MIS",
                    prctyp = priceType,
                    price,
                    qty = quantity,
                    ret = "DAY",
                    symbol_id = resolvedToken,
                    trading_symbol = request.Symbol,
                    transtype = isBuy ? "BUY" : "SELL"
                };

            var orderRequest = new[] { leg };

            _logger.LogInformation(
                "Placing AliceBlue {Variety} order: {Symbol} {Side} qty={Qty} @ {Price}",
                isRobo ? "BO" : "REGULAR", request.Symbol, request.Side, quantity, price);

            var result = await _apiClient.PlaceOrderAsync(orderRequest);
            if (result is null)
                return new BrokerOrderResponse { Success = false, ErrorMessage = "API call failed (null response)" };

            _logger.LogInformation("AliceBlue raw placeOrder response: {Raw}", result.Value.GetRawText());

            // Response is typically an array of legs, each with stat + nestOrderNumber.
            var (orderId, error) = ExtractOrderResult(result.Value);

            if (!string.IsNullOrWhiteSpace(orderId))
            {
                return new BrokerOrderResponse
                {
                    Success = true,
                    OrderId = orderId!,
                    Timestamp = DateTime.UtcNow
                };
            }

            var reason = string.IsNullOrWhiteSpace(error) ? "Order rejected by broker" : error;
            _logger.LogWarning("AliceBlue placeOrder rejected: {Reason}", reason);
            return new BrokerOrderResponse
            {
                Success = false,
                ErrorMessage = reason,
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing AliceBlue order");
            return new BrokerOrderResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<bool> ModifyOrderAsync(string orderId, BrokerOrderRequest request)
    {
        var isBuy = request.Side == OrderSide.Buy;
        var priceType = request.OrderType == OrderType.Market ? "MKT" : "L";
        var modifyRequest = new
        {
            nestOrderNumber = orderId,
            exch = string.IsNullOrWhiteSpace(request.Exchange) ? "NFO" : request.Exchange!,
            trading_symbol = request.Symbol,
            transtype = isBuy ? "BUY" : "SELL",
            prctyp = priceType,
            price = request.OrderType == OrderType.Market
                ? "0"
                : request.Price.ToString("0.##", CultureInfo.InvariantCulture),
            qty = Math.Max(1, (int)Math.Round(request.Quantity, MidpointRounding.AwayFromZero)),
            pCode = request.ProductType == ProductType.Nrml ? "NRML" : "MIS"
        };
        return await _apiClient.ModifyOrderAsync(modifyRequest);
    }

    public async Task<bool> CancelOrderAsync(string orderId)
    {
        var (ok, _) = await _apiClient.CancelOrderAsync(orderId, "NFO");
        return ok;
    }

    public async Task<List<BrokerOrder>> GetOrderBookAsync()
    {
        var orders = new List<BrokerOrder>();
        try
        {
            var result = await _apiClient.GetOrderBookAsync();
            if (result is null)
                return orders;

            foreach (var item in EnumerateObjects(result.Value))
            {
                var orderId = TryGetStringAny(item, "nestOrderNumber", "Nstordno", "orderNumber");
                var symbol = TryGetStringAny(item, "Trsym", "trading_symbol", "Sym");
                if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(symbol))
                    continue;

                orders.Add(new BrokerOrder
                {
                    OrderId = orderId!,
                    Symbol = symbol!,
                    Quantity = ParseDecimal(TryGetStringAny(item, "Qty", "qty")),
                    Price = ParseDecimal(TryGetStringAny(item, "Prc", "price")),
                    Side = ParseOrderSide(TryGetStringAny(item, "Trantype", "transtype")),
                    Status = ParseOrderStatus(TryGetStringAny(item, "Status", "orderStatus", "stat")),
                    FilledQuantity = ParseNullableDecimal(TryGetStringAny(item, "Fillshares", "filledQty")),
                    AveragePrice = ParseNullableDecimal(TryGetStringAny(item, "Avgprc", "averagePrice")),
                    Text = TryGetStringAny(item, "RejReason", "rejreason", "remarks"),
                    CreatedAt = ParseTimestamp(TryGetStringAny(item, "OrderedTime", "orderentrytime", "ExchTime"))
                });
            }

            _logger.LogInformation("Retrieved {Count} AliceBlue orders", orders.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting AliceBlue order book");
        }
        return orders;
    }

    public async Task<List<BrokerPosition>> GetPositionBookAsync()
    {
        var positions = new List<BrokerPosition>();
        try
        {
            var result = await _apiClient.GetPositionBookAsync();
            if (result is null)
                return positions;

            foreach (var item in EnumerateObjects(result.Value))
            {
                var symbol = TryGetStringAny(item, "Tsym", "Trsym", "trading_symbol");
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                positions.Add(new BrokerPosition
                {
                    Symbol = symbol!,
                    Quantity = ParseDecimal(TryGetStringAny(item, "Netqty", "netQty")),
                    AveragePrice = ParseDecimal(TryGetStringAny(item, "NetBuyavgprc", "avgPrice", "Netavgprc")),
                    CurrentPrice = ParseDecimal(TryGetStringAny(item, "LTP", "Ltp")),
                    UnrealizedPnL = ParseDecimal(TryGetStringAny(item, "unrealisedprofitloss", "MtoM", "realisedprofitloss")),
                    OpenedAt = ParseTimestamp(TryGetStringAny(item, "OrderedTime", "orderentrytime", "ExchTime"))
                });
            }

            _logger.LogInformation("Retrieved {Count} AliceBlue positions", positions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting AliceBlue position book");
        }
        return positions;
    }

    public async Task<List<BrokerTrade>> GetTradeBookAsync()
    {
        var trades = new List<BrokerTrade>();
        try
        {
            var result = await _apiClient.GetTradeBookAsync();
            if (result is null)
                return trades;

            foreach (var item in EnumerateObjects(result.Value))
            {
                var symbol = TryGetStringAny(item, "Tsym", "Trsym", "trading_symbol");
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                trades.Add(new BrokerTrade
                {
                    TradeId = TryGetStringAny(item, "Fillid", "fillId", "nestOrderNumber") ?? string.Empty,
                    Symbol = symbol!,
                    Quantity = ParseDecimal(TryGetStringAny(item, "Filledqty", "Fillqty", "qty")),
                    Price = ParseDecimal(TryGetStringAny(item, "Fillprice", "price", "Avgprc")),
                    Side = ParseOrderSide(TryGetStringAny(item, "Trantype", "transtype")),
                    ExecutedAt = ParseTimestamp(TryGetStringAny(item, "Filltime", "filltime", "ExchTime", "OrderedTime"))
                });
            }

            _logger.LogInformation("Retrieved {Count} AliceBlue trades", trades.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting AliceBlue trade book");
        }
        return trades;
    }

    // -------------------------------------------------------------------------
    // Response parsing helpers
    // -------------------------------------------------------------------------

    private static (string? OrderId, string? Error) ExtractOrderResult(JsonElement root)
    {
        foreach (var item in EnumerateObjects(root))
        {
            var stat = TryGetStringAny(item, "stat");
            var orderId = TryGetStringAny(item, "NOrdNo", "nestOrderNumber", "orderNumber");
            if (!string.IsNullOrWhiteSpace(orderId) &&
                (string.IsNullOrWhiteSpace(stat) || string.Equals(stat, "Ok", StringComparison.OrdinalIgnoreCase)))
            {
                return (orderId, null);
            }

            var err = TryGetStringAny(item, "emsg", "Emsg", "errorMessage", "remarks");
            if (!string.IsNullOrWhiteSpace(err))
                return (null, err);
        }

        // Fallback: object-level error fields.
        var topErr = TryGetStringAny(root, "emsg", "Emsg", "errorMessage");
        return (null, topErr);
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object)
                        yield return item;
                break;
            case JsonValueKind.Object:
                yield return element;
                break;
        }
    }

    private static string? TryGetStringAny(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = value.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            return s;
                        break;
                    case JsonValueKind.Number:
                        return value.GetRawText();
                }
            }
        }
        return null;
    }

    private static decimal ParseDecimal(string? value)
        => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static decimal? ParseNullableDecimal(string? value)
        => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateTime ParseTimestamp(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return dt.ToUniversalTime();
        }
        return DateTime.UtcNow;
    }

    private static OrderSide ParseOrderSide(string? value)
        => string.Equals(value, "SELL", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "S", StringComparison.OrdinalIgnoreCase)
            ? OrderSide.Sell
            : OrderSide.Buy;

    private static OrderStatus ParseOrderStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return OrderStatus.Pending;

        return value.Trim().ToLowerInvariant() switch
        {
            "complete" or "executed" or "filled" or "traded" => OrderStatus.Executed,
            "rejected" => OrderStatus.Rejected,
            "cancelled" or "canceled" => OrderStatus.Cancelled,
            "open" or "trigger pending" or "pending" or "after market order req received" => OrderStatus.Accepted,
            _ => OrderStatus.Pending
        };
    }
}
