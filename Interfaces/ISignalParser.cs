using NexusApp.Models;

namespace NexusApp.Interfaces;

/// <summary>
/// Interface for parsing trading signals from text messages
/// </summary>
public interface ISignalParser
{
    /// <summary>
    /// Parses a trading signal from text
    /// </summary>
    Task<ParsedSignal?> ParseAsync(string message, long telegramMessageId, DateTime telegramTimestamp);
}

/// <summary>
/// Represents a parsed trading signal
/// </summary>
public class ParsedSignal
{
    public SignalAction Action { get; set; }
    public string Index { get; set; } = string.Empty;
    public decimal Strike { get; set; }
    public OptionType OptionType { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }
    public List<decimal> Targets { get; set; } = new();
    public DateTime ExpiryDate { get; set; }
    public string OriginalMessage { get; set; } = string.Empty;
    public DateTime SignalTime { get; set; }
    public bool IsValid { get; set; }
    public string? ValidationErrors { get; set; }
    /// <summary>
    /// Manual lot override from the UI. 0 = use account's DefaultQuantity.
    /// </summary>
    public decimal Lots { get; set; }
}
