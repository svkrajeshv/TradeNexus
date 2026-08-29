namespace NexusApp.Models;

/// <summary>
/// Represents an open position in a trading account
/// </summary>
public class Position
{
    public int Id { get; set; }
    
    public int TradingAccountId { get; set; }
    
    public int SignalId { get; set; }
    
    public string Symbol { get; set; } = string.Empty;
    
    public decimal Quantity { get; set; }
    
    public decimal EntryPrice { get; set; }
    
    public decimal CurrentPrice { get; set; }
    
    public decimal? StopLoss { get; set; }
    
    public List<decimal> Targets { get; set; } = new();
    
    public decimal UnrealizedPnL { get; set; }
    
    public decimal UnrealizedPnLPercentage { get; set; }
    
    public DateTime OpenedAt { get; set; }
    
    public DateTime? ClosedAt { get; set; }

    public decimal? ClosingPrice { get; set; }

    public decimal? RealizedPnL { get; set; }

    /// <summary>
    /// True when this position's stop-loss / target must be monitored and exited
    /// by the application (live non-Robo Limit orders, where the broker is NOT
    /// managing brackets). Robo/bracket orders and paper positions leave this
    /// false — paper is simulated locally, Robo is managed by the broker.
    /// </summary>
    public bool ManagedLocally { get; set; }

    // Navigation properties
    public TradingAccount TradingAccount { get; set; } = null!;
    public TradingSignal Signal { get; set; } = null!;

    public override bool Equals(object? obj)
    {
        if (obj is Position other)
        {
            if (Id > 0 && other.Id > 0)
                return Id == other.Id;
        }
        return base.Equals(obj);
    }

    public override int GetHashCode()
    {
        return Id > 0 ? Id.GetHashCode() : base.GetHashCode();
    }
}
