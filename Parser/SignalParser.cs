namespace NexusApp.Parser;

/// <summary>
/// Default / generic signal parser. Handles the standard signal format and acts as
/// the fallback strategy when no channel-specific parser matches. Preserves the
/// original <c>ISignalParser</c> behavior for full backward compatibility.
/// </summary>
public class SignalParser(ILogger<SignalParser> logger, IServiceScopeFactory scopeFactory) : SignalParserBase(logger, scopeFactory)
{
    // Lowest priority — used only when no specialized channel parser matches.
    public override int Priority => 0;
}
