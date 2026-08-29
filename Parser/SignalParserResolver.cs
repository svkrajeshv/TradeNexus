using NexusApp.Interfaces;

namespace NexusApp.Parser;

/// <summary>
/// Selects the appropriate <see cref="IChannelSignalParser"/> for a given Telegram
/// channel. The highest-priority parser whose <see cref="IChannelSignalParser.CanHandle"/>
/// returns true wins. Falls back to the default (lowest-priority) parser when no
/// specialized strategy matches.
/// </summary>
public class SignalParserResolver
{
    private readonly IReadOnlyList<IChannelSignalParser> _parsers;

    public SignalParserResolver(IEnumerable<IChannelSignalParser> parsers)
    {
        // Highest priority first so Resolve can pick the first match.
        _parsers = parsers.OrderByDescending(p => p.Priority).ToList();
    }

    /// <summary>
    /// Returns the best-matching parser for the channel, or the default parser
    /// (lowest priority, CanHandle always true) when nothing else matches.
    /// </summary>
    public IChannelSignalParser Resolve(string? channelName)
    {
        var match = _parsers.FirstOrDefault(p => p.CanHandle(channelName));
        return match ?? _parsers[^1];
    }
}
