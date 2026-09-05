namespace NexusApp.Helpers;

/// <summary>
/// Single source of truth for NSE/BSE session boundaries in IST.
/// <para>
/// Background pollers use this to stay idle outside the session: the broker
/// position book cannot change when the exchange is closed, so polling it then
/// only burns rate-limit budget that the live session needs.
/// </para>
/// </summary>
public static class MarketHours
{
    public static readonly TimeSpan Open = new(9, 15, 0);
    public static readonly TimeSpan Close = new(15, 30, 0);

    /// <summary>
    /// Positions are still settling for a short while after the close (square-off
    /// fills, broker-side realised P&amp;L updates), so polling continues briefly
    /// past 15:30 before going idle.
    /// </summary>
    private static readonly TimeSpan PostCloseGrace = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Pre-open order entry begins at 09:00, so the book is worth reading slightly
    /// before the 09:15 open.
    /// </summary>
    private static readonly TimeSpan PreOpenLead = TimeSpan.FromMinutes(15);

    /// <summary>
    /// True on a trading weekday between the open and close.
    /// Note: exchange holidays are not modelled - the app has no holiday calendar,
    /// so those days behave like normal weekdays.
    /// </summary>
    public static bool IsOpen(DateTime istNow) =>
        IsWeekday(istNow) && istNow.TimeOfDay >= Open && istNow.TimeOfDay <= Close;

    /// <summary>
    /// True while broker state can still change: the pre-open window, the session
    /// itself, and the post-close settling grace period.
    /// </summary>
    public static bool IsPollingWindow(DateTime istNow) =>
        IsWeekday(istNow) &&
        istNow.TimeOfDay >= Open - PreOpenLead &&
        istNow.TimeOfDay <= Close + PostCloseGrace;

    public static bool IsOpenNow() => IsOpen(DateTimeExtensions.IstNow());

    public static bool IsPollingWindowNow() => IsPollingWindow(DateTimeExtensions.IstNow());

    private static bool IsWeekday(DateTime istNow) =>
        istNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}
