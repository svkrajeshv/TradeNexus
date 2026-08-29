using Microsoft.AspNetCore.SignalR;

namespace NexusApp.Hubs;

/// <summary>
/// SignalR hub broadcasting live signal / order / health updates to Blazor clients.
/// </summary>
public sealed class TradingHub(ILogger<TradingHub> logger) : Hub
{
    private readonly ILogger<TradingHub> _logger = logger;

    public override Task OnConnectedAsync()
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
