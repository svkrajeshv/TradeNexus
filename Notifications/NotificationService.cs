using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Helpers;
using NexusApp.Hubs;
using NexusApp.Interfaces;
using NexusApp.Telegram;

namespace NexusApp.Notifications;

/// <summary>
/// Broadcasts notifications to connected Blazor clients via SignalR
/// and sends trade notifications to Telegram.
/// UI components subscribe to <c>Notification</c>, <c>SignalReceived</c> and
/// <c>OrderUpdate</c> events on the trading hub.
/// </summary>
public sealed class NotificationService(
    IHubContext<TradingHub> hub,
    TelegramManager telegramManager,
    IDbContextFactory<TradingDbContext> dbFactory,
    ILogger<NotificationService> logger) : INotificationService
{
    private readonly IHubContext<TradingHub> _hub = hub;
    private readonly TelegramManager _telegramManager = telegramManager;
    private readonly IDbContextFactory<TradingDbContext> _dbFactory = dbFactory;
    private readonly ILogger<NotificationService> _logger = logger;

    public async Task SendSignalNotificationAsync(ParsedSignal signal)
    {
        try
        {
            var channelTag = !string.IsNullOrWhiteSpace(signal.ChannelName) ? $" [{signal.ChannelName}]" : "";
            await _hub.Clients.All.SendAsync("Notification", new
            {
                Kind = "signal",
                Title = $"{signal.Action} {signal.Index} {signal.Strike}{signal.OptionType}{channelTag}",
                Body = $"Entry {signal.EntryPrice} / SL {signal.StopLoss}",
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast signal notification");
        }
    }

    public async Task SendOrderNotificationAsync(string symbol, string action, string details)
    {
        try
        {
            await _hub.Clients.All.SendAsync("Notification", new
            {
                Kind = "order",
                Title = $"{action} {symbol}",
                Body = details,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast order notification");
        }
    }

    public async Task SendErrorNotificationAsync(string error)
    {
        try
        {
            await _hub.Clients.All.SendAsync("Notification", new
            {
                Kind = "error",
                Title = "Error",
                Body = error,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast error notification");
        }
    }

    public async Task SendTelegramOrderPlacedAsync(
        string accountName, 
        bool isPaper, 
        string symbol, 
        string action, 
        decimal entryPrice, 
        decimal quantity, 
        string? orderId = null, 
        string orderStatus = "ACCEPTED")
    {
        try
        {
            var accountType = isPaper ? "Paper Account" : "Real/Live Account";
            var modeBadge = isPaper ? "📝 [PAPER]" : "⚡ [REAL]";
            var istTime = DateTime.UtcNow.ToIstString("hh:mm:ss tt");
            var orderIdDisplay = string.IsNullOrWhiteSpace(orderId) ? "N/A" : orderId;

            var message =
$@"🛒 ORDER PLACED {modeBadge}

• Account: {accountName} ({accountType})
• Order ID: {orderIdDisplay}
• Status: {orderStatus}
• Symbol: {symbol}
• Action: {action.ToUpper()}
• Entry Price: ₹{entryPrice:N2}
• Quantity: {quantity:N0}
• Time: {istTime} IST";

            await _telegramManager.SendOrderNotificationAsync(message);

            // Only a fill is worth an audio cue; a live order that is merely accepted
            // gets its sound later from the order-sync executed path.
            if (orderStatus.Contains("EXECUTED", StringComparison.OrdinalIgnoreCase))
                await SendTradeSoundAsync(isPaper ? "paper-entry" : "live-entry");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send Telegram order placed notification");
        }
    }

    public async Task SendTelegramOrderClosedAsync(string accountName, bool isPaper, string symbol, decimal entryPrice, decimal exitPrice, decimal realizedPnL, decimal quantity, string reason)
    {
        try
        {
            var accountType = isPaper ? "Paper Account" : "Real/Live Account";
            var modeBadge = isPaper ? "📝 [PAPER]" : "⚡ [REAL]";
            var pnlBadge = realizedPnL >= 0 ? "🟢 PROFIT" : "🔴 LOSS";
            var istTime = DateTime.UtcNow.ToIstString("hh:mm:ss tt");

            var message =
$@"🚪 POSITION CLOSED {modeBadge} {pnlBadge}

• Account: {accountName} ({accountType})
• Symbol: {symbol}
• Entry Price: ₹{entryPrice:N2}
• Exit Price: ₹{exitPrice:N2}
• Realized P&L: ₹{realizedPnL:N2}
• Quantity: {quantity:N0}
• Reason: {reason}
• Time: {istTime} IST";

            await _telegramManager.SendOrderNotificationAsync(message);
            await SendTradeSoundAsync(ExitSoundFor(reason, isPaper));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send Telegram order closed notification");
        }
    }

    /// <summary>
    /// Maps a close reason to an audio cue. Stop-loss, target and a manual square-off
    /// each get their own unmistakable sound; every other exit falls back to the
    /// paper/live exit tone so the book is still audible.
    /// </summary>
    private static string ExitSoundFor(string reason, bool isPaper)
    {
        var r = (reason ?? string.Empty).ToLowerInvariant();

        if (r.Contains("stop loss") || r.Contains("stoploss") || r.Contains("sl hit"))
            return "sl-hit";
        if (r.Contains("target"))
            return "target-hit";
        if (r.Contains("square off") || r.Contains("squareoff") || r.Contains("manual"))
            return "squareoff";

        return isPaper ? "paper-exit" : "live-exit";
    }

    public async Task SendTradeSoundAsync(string sound)
    {
        try
        {
            await _hub.Clients.All.SendAsync("TradeSound", new
            {
                Sound = sound,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast trade sound {Sound}", sound);
        }
    }

    public async Task SendTelegramCustomNotificationAsync(string message)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                await _telegramManager.SendOrderNotificationAsync(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send custom Telegram notification");
        }
    }

    public async Task SendTelegramDailyPnlSummaryAsync()
    {
        try
        {
            // Determine today's IST day boundaries expressed in UTC (ClosedAt is stored in UTC).
            var istNow = DateTime.UtcNow.ToIst();
            var istDayStart = istNow.Date;
            var istDayEnd = istDayStart.AddDays(1);
            var utcStart = istDayStart.IstToUtc();
            var utcEnd = istDayEnd.IstToUtc();

            await using var db = await _dbFactory.CreateDbContextAsync();

            var closedToday = await db.Positions
                .AsNoTracking()
                .Where(p => p.ClosedAt != null
                            && p.ClosedAt >= utcStart
                            && p.ClosedAt < utcEnd)
                .Select(p => new ClosedPositionRow(
                    p.Symbol,
                    p.Quantity,
                    p.RealizedPnL ?? 0m,
                    p.TradingAccount.ClientId,
                    p.OpenedAt))
                .ToListAsync();

            var istDate = istDayStart.ToString("dd MMM yyyy");

            // Live and Paper are reported as two separate messages so neither book's
            // numbers get folded into (and confused with) the other's total.
            var liveRows = closedToday
                .Where(p => !string.Equals(p.ClientId, BookScope.PaperClientId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var paperRows = closedToday
                .Where(p => string.Equals(p.ClientId, BookScope.PaperClientId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            await SendBookSummaryAsync("LIVE", "⚡", liveRows, istDate, istNow);
            await SendBookSummaryAsync("PAPER", "📝", paperRows, istDate, istNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send Telegram daily P&L summary");
        }
    }

    private async Task SendBookSummaryAsync(
        string bookLabel,
        string bookIcon,
        List<ClosedPositionRow> rows,
        string istDate,
        DateTime istNow)
    {
        if (rows.Count == 0)
        {
            var noneMessage =
$@"📊 {bookIcon} {bookLabel} P&L SUMMARY — {istDate}

No positions were closed today.";
            await _telegramManager.SendOrderNotificationAsync(noneMessage);
            return;
        }

        // Ordered by entry time (earliest first) rather than grouped by symbol, so
        // repeat trades on the same symbol each show up as their own line in the
        // order they were actually taken.
        var orderedTrades = rows
            .OrderBy(x => x.OpenedAt)
            .ToList();

        var netPnl = rows.Sum(x => x.RealizedPnL);
        var totalTrades = rows.Count;
        var wins = rows.Count(x => x.RealizedPnL > 0);
        var losses = rows.Count(x => x.RealizedPnL < 0);
        var netBadge = netPnl >= 0 ? "🟢 PROFIT" : "🔴 LOSS";

        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"📊 {bookIcon} {bookLabel} P&L SUMMARY — {istDate} {netBadge}");
        lines.AppendLine();
        lines.AppendLine("Executed Symbols:");
        foreach (var t in orderedTrades)
        {
            var symBadge = t.RealizedPnL >= 0 ? "🟢" : "🔴";
            lines.AppendLine($"{symBadge} {t.Symbol}  |  Qty: {t.Quantity:N0}  |  P&L: ₹{t.RealizedPnL:N2}");
        }
        lines.AppendLine();
        lines.AppendLine($"• Total Trades: {totalTrades}");
        lines.AppendLine($"• Wins / Losses: {wins} / {losses}");
        lines.AppendLine($"• Net P&L: ₹{netPnl:N2}");
        lines.Append($"• Time: {istNow:hh:mm:ss tt} IST");

        await _telegramManager.SendOrderNotificationAsync(lines.ToString());
    }

    private sealed record ClosedPositionRow(string Symbol, decimal Quantity, decimal RealizedPnL, string? ClientId, DateTime OpenedAt);
}
