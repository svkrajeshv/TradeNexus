using NexusApp.Interfaces;
using NexusApp.Models;
using System.Text.RegularExpressions;

namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for the "Vip Group 😍😍❤️" channel.
///
/// Signal format (entry + stop-loss only):
///     SENSEX 10 SEP 76300 PE
///     ABOVE :- 300
///     STOPLOSS :- 260
///
/// The targets arrive later as a separate message that <b>tags (replies to)</b> the
/// original call:
///     Target 320/350
///
/// So the first message parses through the generic pipeline (entry from ABOVE,
/// numeric SL, target falls back to <see cref="SignalParserBase.DefaultTargetPoints"/>),
/// and the tagged follow-up is applied to the already-stored signal by
/// <c>TelegramListenerService.TryHandleFollowUpUpdateAsync</c>.
/// No activation message is required.
/// </summary>
public class VipGroupParser(ILogger<VipGroupParser> logger, IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory)
{
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
    /// This channel never writes a BUY/SELL keyword — the direction is implied by
    /// "ABOVE :- 300" (go long once the premium trades above that level). The base
    /// pipeline aborts the parse when no keyword is found, so fall back to Buy
    /// whenever the message carries an ABOVE entry level.
    /// </summary>
    protected override bool ParseAction(string message, ParsedSignal signal)
    {
        if (base.ParseAction(message, signal))
            return true;

        if (Regex.IsMatch(message, PricePattern, RegexOptions.IgnoreCase))
        {
            signal.Action = SignalAction.Buy;
            return true;
        }

        return false;
    }
}
