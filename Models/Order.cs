namespace NexusApp.Models;

/// <summary>
/// Represents a trading order placed through the broker
/// </summary>
public class Order
{
    public int Id { get; set; }
    
    public int SignalId { get; set; }
    
    public int TradingAccountId { get; set; }
    
    public string BrokerId { get; set; } = string.Empty;
    
    public string Symbol { get; set; } = string.Empty;
    
    public decimal Quantity { get; set; }
    
    public decimal Price { get; set; }
    
    public OrderSide Side { get; set; }
    
    public OrderType OrderType { get; set; }
    
    public ProductType ProductType { get; set; }
    
    public OrderStatus Status { get; set; }
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime? ExecutedAt { get; set; }
    
    public decimal? ExecutedPrice { get; set; }
    
    public decimal? FilledQuantity { get; set; }
    
    public string? ErrorMessage { get; set; }

    // Navigation properties
    public TradingSignal Signal { get; set; } = null!;
    public TradingAccount TradingAccount { get; set; } = null!;
}

public enum OrderSide
{
    Buy = 0,
    Sell = 1
}

public enum OrderType
{
    Limit = 0,
    Market = 1,
    StopLoss = 2,
    StopLossLimit = 3
}

public enum ProductType
{
    Mos = 0,
    Nrml = 1,
    Mis = 2
}

public enum OrderStatus
{
    Pending = 0,
    Accepted = 1,
    Executed = 2,
    Rejected = 3,
    Cancelled = 4,
    Failed = 5
}
