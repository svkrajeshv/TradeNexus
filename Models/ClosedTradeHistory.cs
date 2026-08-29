namespace NexusApp.Models;

/// <summary>
/// Durable archive of a closed trade's realised P&amp;L. Unlike <see cref="Position"/>,
/// rows here are never removed by the daily/session cleanup, so the PNL Statements
/// page and daily summaries retain full history. Channel and terminal names are
/// denormalised (copied at archive time) so the record stays complete even after the
/// originating signal or trading account is deleted.
/// </summary>
public class ClosedTradeHistory
{
    public int Id { get; set; }

    /// <summary>The originating <see cref="Position"/> Id. Used to de-duplicate archives.</summary>
    public int PositionId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal? ExitPrice { get; set; }

    public decimal RealizedPnL { get; set; }

    /// <summary>Telegram channel that produced the signal (denormalised).</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Trading account / terminal name (denormalised).</summary>
    public string TerminalName { get; set; } = string.Empty;

    public bool IsPaper { get; set; }

    public DateTime OpenedAt { get; set; }

    public DateTime ClosedAt { get; set; }

    /// <summary>When this archive row was written (UTC).</summary>
    public DateTime ArchivedAt { get; set; }
}
