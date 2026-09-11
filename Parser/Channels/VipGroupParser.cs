using NexusApp.Interfaces;
using NexusApp.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for the "Vip Group 😍😍❤️" channel.
///
/// Format A — entry + stop-loss first, targets arriving in a later message. The follow-up
/// may either <b>tag (reply to)</b> the original call or simply be the next message in the
/// channel:
///     SENSEX 74400 PE
///     ABOVE 345
///     ↳ TGT 370/400/450++
///     ↳ SL 300
///
/// Format B — the full call in one message, entry quoted as a "NEAR" zone:
///     NIFTY 23700 PE
///     NEAR 155-60
///     TGT 170/185/200++
///     SL 140
///
/// In format B the upper bound of the zone is written in shorthand ("155-60" = 155-160),
/// and the entry price is taken as the midpoint of the zone — the same convention the
/// "Buy Range" handling in <see cref="TradeWithMohitAgrawalParser"/> uses.
///
/// Targets and stop-loss flow through the generic pipeline; anything the channel omits
/// falls back to <see cref="SignalParserBase.DefaultTargetPoints"/> /
/// <see cref="SignalParserBase.DefaultSlPoints"/>, and a tagged follow-up is applied to the
/// stored signal by <c>TelegramListenerService.TryHandleFollowUpUpdateAsync</c>.
/// No activation message is required.
/// </summary>
public partial class VipGroupParser(ILogger<VipGroupParser> logger, IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory)
{
    /// <summary>
    /// Matches "NEAR 155-60", "NEAR :- 155 - 160", "NEAR 155–60" (en dash) and the
    /// single-level form "NEAR 155". The second group is optional so both shapes are
    /// handled by one pattern.
    /// </summary>
    [GeneratedRegex(@"NEAR[^\d]*?(\d+(?:\.\d+)?)(?:\s*[-–—]\s*(\d+(?:\.\d+)?))?", RegexOptions.IgnoreCase)]
    private static partial Regex NearZoneRegex();

    /// <summary>
    /// Higher than the generic keyword parsers (priority 10) so an explicit channel
    /// match always outranks the keyword heuristics.
    /// </summary>
    public override int Priority => 20;

    public override bool RequiresActivation => false;

    /// <summary>
    /// Matches the "Vip Group" channel (emoji suffixes in the real title are ignored
    /// because only the keyword is looked up).
    /// </summary>
    public override bool CanHandle(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;
        var normalized = channelName.Replace("_", " ").ToLowerInvariant();
        return normalized.Contains("vip group");
    }

    /// <summary>
    /// Entry is the midpoint of the quoted "NEAR" zone (157.5 for "NEAR 155-60"), or the
    /// single level when no range is given. Falls back to the base ABOVE / BUY parsing.
    /// </summary>
    protected override bool ParseEntryPrice(string message, ParsedSignal signal)
    {
        var match = NearZoneRegex().Match(message);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var low) && low > 0)
        {
            var entry = low;

            if (match.Groups[2].Success && decimal.TryParse(match.Groups[2].Value, out var high) && high > 0)
            {
                high = ExpandShorthandBound(low, high);
                entry = (low + high) / 2m;
            }

            if (entry > 0)
            {
                signal.EntryPrice = entry;
                return true;
            }
        }

        return base.ParseEntryPrice(message, signal);
    }

    /// <summary>
    /// The channel abbreviates the upper bound of a zone by dropping the leading digits
    /// ("155-60" means 155-160). Rebuilds the full number by grafting the quoted digits
    /// onto the lower bound; values already greater than the lower bound are left as-is.
    /// </summary>
    private static decimal ExpandShorthandBound(decimal low, decimal high)
    {
        if (high >= low)
            return high;

        var digits = Math.Max(1, ((long)high).ToString(CultureInfo.InvariantCulture).Length);
        var scale = (decimal)Math.Pow(10, digits);

        var expanded = Math.Floor(low / scale) * scale + high;
        if (expanded < low)
            expanded += scale;

        return expanded;
    }
}
