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
/// A channel-aware signal parser strategy. Each implementation handles the signal
/// format of one (or a family of) Telegram channels. The <see cref="SignalParserResolver"/>
/// selects the highest-priority strategy whose <see cref="CanHandle"/> returns true.
/// </summary>
public interface IChannelSignalParser : ISignalParser
{
    /// <summary>
    /// Higher priority wins when multiple parsers can handle a channel.
    /// The default/generic parser should use the lowest priority.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Returns true when this parser knows how to handle the given channel name.
    /// </summary>
    bool CanHandle(string? channelName);

    /// <summary>
    /// Returns true when a message from this channel should be skipped entirely
    /// (e.g. positional "#BTST TRADE" calls that must never be auto-traded).
    /// </summary>
    bool ShouldSkip(string? message);

    /// <summary>
    /// Returns true when signals from this channel are held until a separate
    /// "ACTIVATED" message arrives (deferred-execution channels).
    /// </summary>
    bool RequiresActivation { get; }

    /// <summary>
    /// Returns true when the given message is the activation/confirmation message
    /// that unlocks a previously received signal that was awaiting activation
    /// (e.g. "ACTIVATED", "🅰ctive all friends 👆❤️"). Only relevant when
    /// <see cref="RequiresActivation"/> is true.
    /// </summary>
    bool IsActivationMessage(string? message);
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
    public string? ChannelName { get; set; }
    /// <summary>
    /// Manual lot override from the UI. 0 = use account's DefaultQuantity.
    /// </summary>
    public decimal Lots { get; set; }
}
