using NexusApp.Interfaces;
using System.Text.RegularExpressions;

namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for the "Trade With Mohit Agrawal" channel. Signals look like:
/// <code>
/// #SENSEX  _76800CE
/// 🏌️‍♂️Buy Range : 400-430
/// 🎯Target : 530/650/820
/// ⛔️Stoploss : 250
/// 📆 Horizon: Intraday
/// </code>
/// Channel-specific rules:
/// <list type="bullet">
///   <item>Only <c>Horizon: Intraday</c> calls are traded; other horizons
///   (e.g. <c>HERO ZERO</c>) are skipped via <see cref="ShouldSkip"/>.</item>
///   <item>Entry price is the <b>midpoint</b> of the "Buy Range" (e.g. 415 of 400-430).</item>
///   <item>Only the <b>first</b> quoted target is used (e.g. 530 of 530/650/820).</item>
/// </list>
/// There is no activation message, so the order triggers via the standard entry-price
/// crossing flow once the CMP reaches the buy price.
/// </summary>
public class TradeWithMohitAgrawalParser(ILogger<TradeWithMohitAgrawalParser> logger, IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory)
{
    // "Buy Range : 400-430" — captures the low (group 1) and high (group 2).
    private const string BuyRangePattern = @"BUY\s*RANGE[^\d]*?(\d+(?:\.\d+)?)\s*[-–]\s*(\d+(?:\.\d+)?)";

    // "Horizon: Intraday" — only these signals are tradable for this channel.
    private const string IntradayHorizonPattern = @"HORIZON[^A-Z]*INTRADAY";

    public override int Priority => 10;

    // No activation message on this channel — execute via CMP crossing directly.
    public override bool RequiresActivation => false;

    /// <summary>Matches the "Trade With Mohit Agrawal" channel (or names containing "mohit agrawal").</summary>
    public override bool CanHandle(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;
        var n = channelName.Replace("_", " ").ToLowerInvariant();
        return n.Contains("mohit agrawal") || n.Contains("trade with mohit");
    }

    /// <summary>
    /// Skip any signal that is not an Intraday horizon (e.g. "Horizon: HERO ZERO").
    /// Only "Horizon: Intraday" calls are used for live monitoring and trading.
    /// </summary>
    public override bool ShouldSkip(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return true;
        return !Regex.IsMatch(message, IntradayHorizonPattern, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Entry price is the midpoint of the "Buy Range" (e.g. 415 for "Buy Range : 400-430").
    /// Falls back to the base ABOVE / BUY NNN parsing if no range is present.
    /// </summary>
    protected override bool ParseEntryPrice(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, BuyRangePattern, RegexOptions.IgnoreCase);
        if (match.Success
            && decimal.TryParse(match.Groups[1].Value, out var low)
            && decimal.TryParse(match.Groups[2].Value, out var high))
        {
             var entry = (low + high) / 2m;
            if (entry > 0)
            {
                signal.EntryPrice = entry;
                return true;
            }
        }

        return base.ParseEntryPrice(message, signal);
    }

    /// <summary>
    /// Uses only the first quoted target (e.g. 530 for "Target : 530/650/820").
    /// Falls back to the base default-target behavior when no numeric target is present.
    /// </summary>
    protected override bool ParseTargets(string message, ParsedSignal signal)
    {
        var match = Regex.Match(message, TargetPattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var first = Regex.Match(match.Groups[1].Value.Trim(), @"^\d+(?:\.\d+)?");
            if (first.Success && decimal.TryParse(first.Value, out var target) && target > 0)
            {
                signal.Targets.Add(target);
                return true;
            }
        }

        return base.ParseTargets(message, signal);
    }
}
