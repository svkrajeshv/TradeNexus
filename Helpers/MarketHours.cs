namespace NexusApp.Helpers;

/// <summary>
/// Single source of truth for exchange session boundaries in IST.
/// <para>
/// Background pollers use this to stay idle outside the session: the broker
/// position book cannot change when the exchange is closed, so polling it then
/// only burns rate-limit budget that the live session needs.
/// </para>
/// <para>
/// Two segments are modelled. NSE/BSE runs 09:15-15:30; MCX runs 09:00-23:30.
/// The parameterless members remain equity-only so every pre-existing caller keeps
/// its current behaviour; commodity callers opt in via the segment overloads.
/// </para>
/// </summary>
public static class MarketHours
{
    public static readonly TimeSpan Open = new(9, 15, 0);
    public static readonly TimeSpan Close = new(15, 30, 0);

    /// <summary>MCX commodity session start (IST).</summary>
    public static readonly TimeSpan CommodityOpen = new(9, 0, 0);

    /// <summary>
    /// MCX commodity session end (IST). MCX extends to 23:55 during US daylight
    /// saving; 23:30 is the conservative year-round close.
    /// </summary>
    public static readonly TimeSpan CommodityClose = new(23, 30, 0);

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
    public static bool IsOpen(DateTime istNow) => IsOpen(istNow, MarketSegment.Equity);

    /// <summary>True on a trading weekday within the given segment's session.</summary>
    public static bool IsOpen(DateTime istNow, MarketSegment segment)
    {
        var (open, close) = BoundsFor(segment);
        return IsWeekday(istNow) && istNow.TimeOfDay >= open && istNow.TimeOfDay <= close;
    }

    /// <summary>
    /// True while broker state can still change: the pre-open window, the session
    /// itself, and the post-close settling grace period.
    /// </summary>
    public static bool IsPollingWindow(DateTime istNow) => IsPollingWindow(istNow, MarketSegment.Equity);

    /// <summary>
    /// True while broker state can still change for the given segment, including the
    /// pre-open lead and the post-close settling grace period.
    /// </summary>
    public static bool IsPollingWindow(DateTime istNow, MarketSegment segment)
    {
        var (open, close) = BoundsFor(segment);
        return IsWeekday(istNow) &&
               istNow.TimeOfDay >= open - PreOpenLead &&
               istNow.TimeOfDay <= close + PostCloseGrace;
    }

    public static bool IsOpenNow() => IsOpen(DateTimeExtensions.IstNow());

    public static bool IsOpenNow(MarketSegment segment) => IsOpen(DateTimeExtensions.IstNow(), segment);

    public static bool IsPollingWindowNow() => IsPollingWindow(DateTimeExtensions.IstNow());

    public static bool IsPollingWindowNow(MarketSegment segment) =>
        IsPollingWindow(DateTimeExtensions.IstNow(), segment);

    /// <summary>
    /// True when any tradable segment is in its polling window.
    /// <para>
    /// <paramref name="includeCommodity"/> reflects the <c>Mcx.Enabled</c> master
    /// toggle: when commodity trading is off this collapses to the equity window, so
    /// poller timing stays byte-identical to its pre-MCX behaviour.
    /// </para>
    /// </summary>
    public static bool IsAnySegmentPollingWindowNow(bool includeCommodity)
    {
        var istNow = DateTimeExtensions.IstNow();
        return IsPollingWindow(istNow, MarketSegment.Equity) ||
               (includeCommodity && IsPollingWindow(istNow, MarketSegment.Commodity));
    }

    private static (TimeSpan Open, TimeSpan Close) BoundsFor(MarketSegment segment) =>
        segment == MarketSegment.Commodity ? (CommodityOpen, CommodityClose) : (Open, Close);

    private static bool IsWeekday(DateTime istNow) =>
        istNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}
