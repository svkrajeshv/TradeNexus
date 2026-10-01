namespace NexusApp.Parser;

/// <summary>
/// Lifetime rules for calls from tag-entry channels (parsers with
/// <c>AlwaysAwaitEntry</c>): Vip Group, BANKNIFTY EXPRESS, TRADE WITH PIHU,
/// GUJARATI TRADER and Stock mantra. These calls are entered only when the channel
/// posts the entry price, so they may legitimately wait longer than other signals.
/// </summary>
public static class TagEntryPolicy
{
    /// <summary>Maximum age of a waiting call before it expires unfilled.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>No new tag-entry positions are opened at or after this IST time.</summary>
    public static readonly TimeSpan LastEntryTimeIst = new(15, 0, 0);
}
