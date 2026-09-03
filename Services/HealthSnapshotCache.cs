namespace NexusApp.Services;

/// <summary>
/// The last health figures broadcast by the health monitor.
/// </summary>
public sealed record HealthSnapshot(
    bool BrokerConnected,
    bool BrokerPnlAvailable,
    decimal LiveUnrealized,
    decimal LiveRealized,
    DateTime TimestampUtc);

/// <summary>
/// Keeps the most recent <see cref="HealthSnapshot"/> so a page that loads (or a
/// book toggle that is clicked) between two 30s health ticks can render the live
/// figures immediately instead of showing zeros until the next broadcast.
/// </summary>
public sealed class HealthSnapshotCache
{
    private HealthSnapshot? _latest;

    public HealthSnapshot? Latest
    {
        get => Volatile.Read(ref _latest);
        set => Volatile.Write(ref _latest, value);
    }
}
