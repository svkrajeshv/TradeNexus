using NexusApp.Helpers;
using Serilog.Core;
using Serilog.Events;

namespace NexusApp.Startup;

/// <summary>
/// Adds an <c>IstTimestamp</c> property so log output is always stamped in Indian
/// Standard Time regardless of the host machine's timezone. Serilog's built-in
/// <c>{Timestamp}</c> renders in the server's local time, which silently drifts from
/// the exchange clock when the app runs on a non-IST host (e.g. a cloud VM in UTC).
/// </summary>
internal sealed class IstTimestampEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var ist = logEvent.Timestamp.UtcDateTime.ToIst();
        logEvent.AddOrUpdateProperty(
            propertyFactory.CreateProperty("IstTimestamp", ist.ToString("yyyy-MM-dd HH:mm:ss.fff")));
    }
}
