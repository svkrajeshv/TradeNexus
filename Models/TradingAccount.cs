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

    // Navigation properties
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<Position> Positions { get; set; } = new List<Position>();
}
