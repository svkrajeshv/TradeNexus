namespace NexusApp.Models;

/// <summary>
/// Represents a trading account configured in the application
/// </summary>
public class TradingAccount
{
    public int Id { get; set; }
    
    public string Name { get; set; } = string.Empty;
    
    public string BrokerType { get; set; } = "AngelOne";
    
    public string ClientId { get; set; } = string.Empty;
    
    public bool IsEnabled { get; set; } = true;
    
    public bool IsDefault { get; set; } = false;
    
    public decimal DailyMaxLoss { get; set; } = 1000;
    
    public decimal DailyMaxProfit { get; set; } = 5000;
    
    public int MaxOpenPositions { get; set; } = 5;
    
    public int MaxTradesPerDay { get; set; } = 10;
    
    public decimal DefaultQuantity { get; set; } = 1;
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// True when this account is a paper/simulated account (ClientId is "PAPER").
    /// Paper accounts route to the PaperTradingEngine; real accounts place live broker orders.
    /// </summary>
    public bool IsPaperAccount => string.Equals(ClientId, "PAPER", StringComparison.OrdinalIgnoreCase);

    // Navigation properties
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<Position> Positions { get; set; } = new List<Position>();
}
