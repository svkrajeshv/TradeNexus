using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NexusApp.BackgroundServices;
using NexusApp.Interfaces;

namespace NexusApp.Notifications;

/// <summary>
/// Broadcasts notifications to connected Blazor clients via SignalR.
/// UI components subscribe to <c>Notification</c>, <c>SignalReceived</c> and
/// <c>OrderUpdate</c> events on the trading hub.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly IHubContext<TradingHub> _hub;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IHubContext<TradingHub> hub, ILogger<NotificationService> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task SendSignalNotificationAsync(ParsedSignal signal)
    {
        try
        {
            await _hub.Clients.All.SendAsync("Notification", new
            {
                Kind = "signal",
                Title = $"{signal.Action} {signal.Index} {signal.Strike}{signal.OptionType}",
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
}
