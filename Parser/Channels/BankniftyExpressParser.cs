using NexusApp.Interfaces;
using NexusApp.Models;
using System.Text.RegularExpressions;

namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for the "Banknifty Express" channel
/// (https://t.me/banknifty_nifty_sensex_express).
///
/// Signal format (SENSEX / NIFTY / BANKNIFTY / FINNIFTY option index):
///     SENSEX 01 SEP 77100 PE
///     ABOVE :- 360
///     STOPLOSS :- VIP
///
/// These signals never quote a TARGET and the stop-loss is usually a non-numeric
/// placeholder ("VIP"), so the generic pipeline defaults apply: target =
/// entry + <see cref="SignalParserBase.DefaultTargetPoints"/> (20 pts) and
/// stop-loss = entry - <see cref="SignalParserBase.DefaultSlPoints"/> (50 pts).
/// No activation message is required.
/// </summary>
public class BankniftyExpressParser(ILogger<BankniftyExpressParser> logger, IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory)
{
    /// <summary>
    /// Higher than the generic keyword parsers (priority 10). The real channel name is
    /// "Banknifty express (Intradaycalls)", which also satisfies
    /// <see cref="IntradayNiftyParser"/>'s "intraday" + "calls" keyword match. That parser
    /// requires a follow-up ACTIVATED message, so it would silently park every signal in
    /// AwaitingActivation. An explicit channel match must outrank the keyword heuristics.
    /// </summary>
    public override int Priority => 20;

    public override bool RequiresActivation => false;

    /// <summary>
    /// Matches the "Banknifty Express" / "banknifty_nifty_sensex_express" channel.
    /// </summary>
    public override bool CanHandle(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;
        var normalized = channelName.Replace("_", " ").ToLowerInvariant();
        return normalized.Contains("banknifty express") ||
               (normalized.Contains("banknifty") && normalized.Contains("express"));
    }

    /// <summary>
    /// This channel never writes a BUY/SELL keyword — the direction is implied by
    /// "ABOVE :- 368" (go long once the premium trades above that level). The base
    /// pipeline aborts the entire parse when no keyword is found, so fall back to Buy
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
