namespace NexusApp.Models;

/// <summary>
/// Represents a parsed trading signal received from Telegram
/// </summary>
public class TradingSignal
{
    public int Id { get; set; }
    
    public long TelegramMessageId { get; set; }
    
    public string OriginalMessage { get; set; } = string.Empty;
    
    public DateTime TelegramTimestamp { get; set; }
    
    public DateTime ReceivedTimestamp { get; set; }
    
    public DateTime ProcessedTimestamp { get; set; }
    
    public SignalAction Action { get; set; }
    
    public string Index { get; set; } = string.Empty;
    
    public decimal Strike { get; set; }
    
    public OptionType OptionType { get; set; }
    
    public decimal EntryPrice { get; set; }
    
    public decimal StopLoss { get; set; }
    
    public List<decimal> Targets { get; set; } = new();
    
    public DateTime ExpiryDate { get; set; }
    
    public SignalStatus Status { get; set; }
    
    public string? Symbol { get; set; }
    
    public decimal SignalDelayMs { get; set; }
    
    public string? ChannelName { get; set; }

    /// <summary>
    /// Navigation property for related orders
    /// </summary>
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}

public enum SignalAction
{
    Buy = 0,
    Sell = 1
}

public enum OptionType
{
    Ce = 0,
    Pe = 1
}

public enum SignalStatus
{
    Received = 0,
    Parsed = 1,
    Pending = 2,
    Executed = 3,
    Failed = 4,
    Ignored = 5,
    AwaitingActivation = 6,
    AwaitingEntry = 7
}
