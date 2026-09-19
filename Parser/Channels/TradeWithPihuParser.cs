using NexusApp.Interfaces;
using System.Text.RegularExpressions;

namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for the "TRADE WITH PIHU" channel. Signals look like:
///   SENSEX 77300 PE / BUY above 10 / TGT paid / SL paid
///   Sensex 77500 ce / Buy near 83 / Tgt paid / Sl paid
/// This channel has NO activation message, so <see cref="RequiresActivation"/> is
/// false and the order is triggered by the standard entry-price crossing flow once
/// the CMP reaches the buy price.
///
/// Targets and stop-losses are frequently quoted as "paid" / "premium group" / omitted
/// (no number). In those cases we fall back to configurable defaults from settings
/// (DefaultTargetPoints, default 20; DefaultStopLossPoints, default 50) instead of
/// failing to parse the signal.
/// </summary>
public partial class TradeWithPihuParser(ILogger<TradeWithPihuParser> logger, IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory)
{
    // Handles "Buy near 83", "Buy around 83", "Buy @ 83" — the base only handles
    // the "ABOVE" keyword and the plain "BUY NNN" form.
    [GeneratedRegex(@"(?:NEAR|AROUND|@)[^\d]*?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex NearPriceRegex();

    public override int Priority => 10;

    // No activation message on this channel — execute via CMP crossing directly.
    public override bool RequiresActivation => false;

    /// <summary>Matches the "TRADE WITH PIHU" channel (or names containing "pihu").</summary>
    public override bool CanHandle(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;
        var n = channelName.Replace("_", " ").ToLowerInvariant();
        return n.Contains("trade with pihu") || n.Contains("pihu");
    }

    /// <summary>
    /// Adds a "near/around/@" entry-price fallback on top of the base ABOVE / BUY NNN parsing.
    /// The non-numeric Target/SL defaults are handled by the base parser for all channels.
    /// </summary>
    protected override bool ParseEntryPrice(string message, ParsedSignal signal)
    {
        if (base.ParseEntryPrice(message, signal)) return true;

        var match = NearPriceRegex().Match(message);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var price) && price > 0)
        {
            signal.EntryPrice = price;
            return true;
        }
        return false;
    }
}
