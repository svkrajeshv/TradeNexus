namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for the "GUJARATI TRADER" channel.
///
/// Signal format (SENSEX / NIFTY / BANKNIFTY / FINNIFTY option index):
///     ‼️ SENSEX 77800 PE ‼️
///     ✅ BUY ABOVE - 250
///     ⚠️ TGT - 280/310/350+
///     🔻 SL -210
///     #27Aug Expiry
///
/// The generic pipeline already handles every field: index/strike/option-type,
/// "BUY ABOVE - NNN" entry, "TGT - a/b/c+" targets, "SL -NNN" stop-loss and the
/// "#27Aug Expiry" date. No activation message is required.
/// </summary>
public class GujaratiTraderParser(ILogger<GujaratiTraderParser> logger, IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory), NexusApp.Interfaces.IChannelSignalParser
{
    public override int Priority => 10;

    public override bool RequiresActivation => false;

    /// <summary>
    /// Calls are entered only when the channel posts the entry price as a tagged or
    /// follow-up message (e.g. "250❤️").
    /// </summary>
    public bool AlwaysAwaitEntry => true;

    public bool TryParsePriceTag(string? message, out decimal price) =>
        VipGroupParser.TryParseBarePrice(message, out price);

    /// <summary>
    /// Matches the "GUJARATI TRADER" channel.
    /// </summary>
    public override bool CanHandle(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;
        var normalized = channelName.Replace("_", " ").ToLowerInvariant();
        return normalized.Contains("gujarati trader") ||
               (normalized.Contains("gujarati") && normalized.Contains("trader"));
    }
}
