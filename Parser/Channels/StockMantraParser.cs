using NexusApp.Interfaces;
using System.Text.RegularExpressions;

namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for the "Stock mantra Index SEBI Registered" channel. Signals look like:
///   NIFTY 22850 pe / Near 140
///   BANK NIFTY 54300 PE / NEAR 330 / Tgt open / Sl follow
/// The channel then posts the running price as bare numbers ("330", "333", ...).
/// Calls are entered only when a posted price equals the entry (same rule as Vip Group).
/// Non-numeric "Tgt open" / "Sl follow" fall back to the configured default points.
/// </summary>
public partial class StockMantraParser(ILogger<StockMantraParser> logger, IServiceScopeFactory scopeFactory)
    : SignalParserBase(logger, scopeFactory), IChannelSignalParser
{
    [GeneratedRegex(@"(?:NEAR|AROUND|ABOVE|@)[^\d]*?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex NearPriceRegex();

    // "BANK NIFTY" / "FIN NIFTY" written with a space would otherwise match plain NIFTY.
    [GeneratedRegex(@"\b(BANK|FIN|MIDCP)\s+NIFTY\b", RegexOptions.IgnoreCase)]
    private static partial Regex SplitIndexRegex();

    public override int Priority => 20;

    public override bool RequiresActivation => false;

    public bool AlwaysAwaitEntry => true;

    public bool TryParsePriceTag(string? message, out decimal price) =>
        VipGroupParser.TryParseBarePrice(message, out price);

    public override bool CanHandle(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;
        var n = channelName.Replace("_", " ").ToLowerInvariant();
        return n.Contains("stock mantra") || n.Contains("stockmantra");
    }

    protected override ParsedSignal? Parse(string message, long telegramMessageId, DateTime telegramTimestamp)
    {
        var signal = base.Parse(SplitIndexRegex().Replace(message ?? string.Empty, "$1NIFTY"), telegramMessageId, telegramTimestamp);
        if (signal is not null) signal.OriginalMessage = message ?? string.Empty;
        return signal;
    }

    protected override bool ParseEntryPrice(string message, ParsedSignal signal)
    {
        var match = NearPriceRegex().Match(message);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var price) && price > 0)
        {
            signal.EntryPrice = price;
            return true;
        }

        return base.ParseEntryPrice(message, signal);
    }
}
