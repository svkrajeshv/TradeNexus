using NexusApp.Models;

namespace NexusApp.Interfaces;

/// <summary>
/// Interface for broker implementations. Abstracts broker-specific functionality.
/// </summary>
public interface IBroker
{
    string BrokerName { get; }
    
    /// <summary>
    /// Authenticates with the broker and returns credentials
    /// </summary>
    Task<bool> AuthenticateAsync(string clientId, string password, string? twoFactorCode = null);
    
    /// <summary>
    /// Logs out from the broker
    /// </summary>
    Task<bool> LogoutAsync();
    
    /// <summary>
    /// Refreshes authentication token
    /// </summary>
    Task<bool> RefreshTokenAsync();
    
    /// <summary>
    /// Gets account balance and margin information
    /// </summary>
    Task<BrokerAccountInfo?> GetAccountInfoAsync();
    
    /// <summary>
    /// Searches for an instrument in the broker's database
    /// </summary>
    Task<BrokerInstrument?> SearchInstrumentAsync(string symbol);
    
    /// <summary>
    /// Gets live price for an instrument
    /// </summary>
    Task<decimal> GetLiveQuoteAsync(string symbol);

    /// <summary>
    /// Releases any live price feed (e.g. WebSocket subscription) held for a symbol,
    /// typically once its position is closed and no longer needs monitoring.
    /// Default is a no-op for brokers that do not maintain a streaming feed.
    /// </summary>
    Task ReleaseSymbolFeedAsync(string symbol) => Task.CompletedTask;
    
    /// <summary>
    /// Places a new order
    /// </summary>
    Task<BrokerOrderResponse> PlaceOrderAsync(BrokerOrderRequest request);
    
    /// <summary>
    /// Modifies an existing order
    /// </summary>
    Task<bool> ModifyOrderAsync(string orderId, BrokerOrderRequest request);
    
    /// <summary>
    /// Cancels an order
    /// </summary>
    Task<bool> CancelOrderAsync(string orderId);

    /// <summary>
    /// Squares off an open bracket/Robo (BO) position. The entry leg is already
    /// complete and therefore not cancellable — brokers exit a bracket by cancelling
    /// its still-open child leg (stop-loss / target), which closes the position at
    /// market. Default is a no-op for brokers without bracket support.
    /// </summary>
    /// <param name="parentOrderId">Broker order id of the bracket's entry leg.</param>
    /// <param name="symbol">Trading symbol, used when the child legs cannot be matched by parent id.</param>
    Task<bool> ExitBracketOrderAsync(string parentOrderId, string symbol) => Task.FromResult(false);

    /// <summary>
    /// Gets order book for the account
    /// </summary>
    Task<List<BrokerOrder>> GetOrderBookAsync();
    
    /// <summary>
    /// Gets position book for the account
    /// </summary>
    Task<List<BrokerPosition>> GetPositionBookAsync();
    
    /// <summary>
    /// Gets trade book for the account
    /// </summary>
    Task<List<BrokerTrade>> GetTradeBookAsync();
    
    /// <summary>
    /// Checks if connection is active
    /// </summary>
    bool IsConnected { get; }
}

/// <summary>
/// Response from broker for order placement
/// </summary>
public class BrokerOrderResponse
{
    public bool Success { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Request to place an order with broker
/// </summary>
public class BrokerOrderRequest
{
    public string Symbol { get; set; } = string.Empty;
    public string? SymbolToken { get; set; }
    public string? Exchange { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public OrderSide Side { get; set; }
    public OrderType OrderType { get; set; }
    public ProductType ProductType { get; set; }

    // Robo / Bracket order fields
    /// <summary>Angel One variety: "NORMAL" (plain limit) or "ROBO" (bracket with SL + target).</summary>
    public string Variety { get; set; } = "NORMAL";
    /// <summary>Target profit offset in absolute points from entry price.</summary>
    public decimal SquareOffPoints { get; set; }
    /// <summary>Stop-loss offset in absolute points from entry price.</summary>
    public decimal StopLossPoints { get; set; }
    /// <summary>Trailing stop-loss step in absolute points (0 = disabled).</summary>
    public decimal TrailingStopLossPoints { get; set; }
}

/// <summary>
/// Instrument information from broker
/// </summary>
public class BrokerInstrument
{
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Token { get; set; }
    public string? SymbolToken { get; set; }
    public decimal LotSize { get; set; }
    public decimal TickSize { get; set; }
    public string ExchangeSegment { get; set; } = string.Empty;
}

/// <summary>
/// Account information from broker
/// </summary>
public class BrokerAccountInfo
{
    public decimal AvailableMargin { get; set; }
    public decimal UsedMargin { get; set; }
    public decimal TotalMargin { get; set; }
    public decimal CashBalance { get; set; }
    public decimal Portfolio { get; set; }
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Order information from broker
/// </summary>
public class BrokerOrder
{
    public string OrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public OrderSide Side { get; set; }
    public OrderStatus Status { get; set; }
    public decimal? FilledQuantity { get; set; }
    public decimal? AveragePrice { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Broker status message / rejection reason (surfaced in Orders grid Remarks column).</summary>
    public string? Text { get; set; }
}

/// <summary>
/// Position information from broker
/// </summary>
public class BrokerPosition
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal UnrealizedPnL { get; set; }
    /// <summary>
    /// Booked P&amp;L for the day on this contract, as reported by the broker.
    /// Non-zero even when <see cref="Quantity"/> is 0 (fully squared-off intraday leg).
    /// </summary>
    public decimal RealizedPnL { get; set; }
    public DateTime OpenedAt { get; set; }

    /// <summary>
    /// Broker instrument token, used to subscribe to live price ticks.
    /// </summary>
    public string? SymbolToken { get; set; }

    /// <summary>
    /// Exchange segment of the instrument (NSE, NFO, MCX, ...).
    /// </summary>
    public string? Exchange { get; set; }
}

/// <summary>
/// Trade information from broker
/// </summary>
public class BrokerTrade
{
    public string TradeId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public OrderSide Side { get; set; }
    public DateTime ExecutedAt { get; set; }
}
