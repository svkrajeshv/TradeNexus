using System.Text.RegularExpressions;

namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for the "ABC BTST TRADE" channel. Reuses the generic parsing
/// pipeline but skips positional "#BTST TRADE" messages, which must never be
/// auto-executed.
/// </summary>
public class AbcBtstSignalParser(ILogger<AbcBtstSignalParser> logger, IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory)
{
    public override int Priority => 10;

    // ABC BTST intraday signals are held until the "Active all friends" tag arrives.
    public override bool RequiresActivation => true;

    /// <summary>
    /// Matches the "ABC BTST TRADE" channel (or similar names).
    /// </summary>
    public override bool CanHandle(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;
        var n = channelName.Replace("_", " ").ToLowerInvariant();
        return n.Contains("abc btst") || (n.Contains("abc") && n.Contains("btst"));
    }

    /// <summary>
    /// Skip "#BTST TRADE" positional calls (Buy Today Sell Tomorrow) which should
    /// not be auto-executed.
    /// </summary>
    public override bool ShouldSkip(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return Regex.IsMatch(message, @"#?\bBTST\s+TRADE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The ABC BTST order should only execute once the channel tags the signal with
    /// "🅰ctive all friends 👆❤️". The leading "🅰" is an emoji glyph (not the letter
    /// 'A'), so we match on the case-insensitive substring "CTIVE ALL FRIEND", which
    /// catches both the stylized ("🅰ctive all friends") and plain ("Active all friend")
    /// variants, plural or singular.
    /// </summary>
    public override bool IsActivationMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("CTIVE ALL FRIEND", StringComparison.OrdinalIgnoreCase);
    }
}
