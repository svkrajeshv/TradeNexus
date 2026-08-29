namespace NexusApp.Parser.Channels;

/// <summary>
/// Parser strategy for the "Zero to Hero" / intraday Nifty channels. Signals from
/// these channels are held until a separate "ACTIVATED" message arrives, so
/// <see cref="RequiresActivation"/> is true. The parsing itself reuses the generic
/// pipeline.
/// </summary>
public class IntradayNiftyParser(ILogger<IntradayNiftyParser> logger, IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory)
{
    public override int Priority => 10;

    public override bool RequiresActivation => true;

    /// <summary>
    /// Matches "Zero to Hero" / intraday trading / intraday nifty channels.
    /// </summary>
    public override bool CanHandle(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;
        var normalized = channelName.Replace("_", " ").ToLowerInvariant();
        return normalized.Contains("zero to hero") ||
               (normalized.Contains("zero") && normalized.Contains("hero")) ||
               (normalized.Contains("intraday") && (normalized.Contains("trading") || normalized.Contains("calls") || normalized.Contains("nifty")));
    }

    /// <summary>
    /// Zero-to-Hero / intraday signals are activated by a follow-up "ACTIVATED" message.
    /// </summary>
    public override bool IsActivationMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("ACTIVATED", StringComparison.OrdinalIgnoreCase);
    }
}
