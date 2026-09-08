using NexusApp.Helpers;
using NexusApp.Interfaces;
using NexusApp.Models;
using System.Text.RegularExpressions;

namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for dedicated MCX commodity channels (CRUDEOIL, NATURALGAS,
/// GOLD, SILVER).
/// <para>
/// Commodity underlying and strike recognition live in <see cref="SignalParserBase"/>
/// so that mixed channels - which post both equity and commodity calls - parse
/// commodity signals too. This strategy adds only what is specific to a
/// commodity-dedicated channel's wording.
/// </para>
/// <para>
/// Confirmed format ("Trade with Mohit Agrawal Commodity Trading"):
/// <code>
/// COMMODITY MCX TRADE
///  CRUDEOIL 8700CE
/// NEAR LEVEL - 370
/// TARGET - 420/465/525
/// STOPLOSS - 320
/// EXPIRY - SEP
/// </code>
/// Channel-specific rules:
/// <list type="bullet">
///   <item>No BUY/SELL keyword - option buying is implied, so the action defaults to BUY.</item>
///   <item>Entry is quoted as "NEAR LEVEL" rather than the equity channels' "ABOVE".</item>
///   <item>Strike and option type are joined ("8700CE") or spaced ("275 CE").</item>
///   <item>Expiry is a bare month ("SEP"); the listed contract date comes from the
///   instrument master, since MCX expiry days vary per commodity.</item>
/// </list>
/// </para>
/// </summary>
public partial class McxSignalParser(
    ILogger<McxSignalParser> logger,
    IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory)
{
    /// <summary>
    /// "NEAR LEVEL - 370" / "NEAR LEVEL 15.20". Mirrors the base class's
    /// <c>PricePattern</c> shape so emoji, arrows and punctuation between the keyword
    /// and the number are skipped.
    /// </summary>
    [GeneratedRegex(@"NEAR\s*LEVEL[^\d]*?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex NearLevelRegex();

    /// <summary>
    /// "EXPIRY - SEP" / "EXPIRY: AUGUST". Month only, with no day - the base
    /// <c>ExpiryPattern</c> requires a leading day and would otherwise capture an
    /// unrelated number from the message.
    /// </summary>
    [GeneratedRegex(@"EXPIRY[^A-Z0-9]*(JANUARY|FEBRUARY|MARCH|APRIL|MAY|JUNE|JULY|AUGUST|SEPTEMBER|OCTOBER|NOVEMBER|DECEMBER|JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)", RegexOptions.IgnoreCase)]
    private static partial Regex MonthOnlyExpiryRegex();


    /// <summary>
    /// Above the generic keyword parsers so an MCX channel is never claimed by a
    /// heuristic equity parser that could not parse a commodity underlying anyway.
    /// </summary>
    public override int Priority => 30;

    public override bool RequiresActivation => false;

    /// <summary>
    /// Claims channels that identify themselves as commodity/MCX feeds, or that name
    /// one of the supported commodities directly.
    /// </summary>
    public override bool CanHandle(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;

        var normalized = channelName.Replace("_", " ").ToLowerInvariant();
        if (normalized.Contains("mcx") || normalized.Contains("commodity") || normalized.Contains("commodities"))
            return true;

        return MarketSegments.CommodityUnderlyings.Any(c => normalized.Contains(c, StringComparison.InvariantCultureIgnoreCase));
    }

    /// <summary>
    /// These signals carry no BUY/SELL keyword - the channel posts option-buying calls
    /// only, and the base implementation would reject every message for a missing
    /// action. Falls back to BUY when no keyword is present, but still honours an
    /// explicit SELL if the channel ever starts quoting one.
    /// </summary>
    protected override bool ParseAction(string message, ParsedSignal signal)
    {
        if (base.ParseAction(message, signal))
            return true;

        signal.Action = SignalAction.Buy;
        return true;
    }

    /// <summary>
    /// Entry is quoted as "NEAR LEVEL - 370". Falls back to the base ABOVE / BUY NNN
    /// parsing so a differently-worded message still resolves.
    /// </summary>
    protected override bool ParseEntryPrice(string message, ParsedSignal signal)
    {
        var match = NearLevelRegex().Match(message);
        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var price) && price > 0)
        {
            signal.EntryPrice = price;
            return true;
        }

        return base.ParseEntryPrice(message, signal);
    }

    /// <summary>
    /// Expiry is quoted as a bare month ("EXPIRY - SEP"), not a date, because MCX
    /// options are monthly contracts whose expiry day differs per commodity. The
    /// month is recorded as the last day of that month: it is a *month marker*, not a
    /// tradable date, and <see cref="TradingEngine.SymbolBuilder"/> maps it onto the
    /// contract actually listed in that month. The last day is used (rather than the
    /// first) so the marker never looks like a past expiry mid-month.
    /// </summary>
    protected override bool ParseExpiryDate(string message, ParsedSignal signal)
    {
        var match = MonthOnlyExpiryRegex().Match(message);
        if (match.Success)
        {
            var month = ParseMonth(match.Groups[1].Value);
            if (month > 0)
            {
                var istToday = DateTimeExtensions.IstToday();
                var year = month < istToday.Month ? istToday.Year + 1 : istToday.Year;

                signal.ExpiryDate = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Unspecified);
                return true;
            }
        }

        return base.ParseExpiryDate(message, signal);
    }
}
