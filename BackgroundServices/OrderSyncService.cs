using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Reconciles locally-stored <see cref="Order"/> rows with the broker's live
/// order book. Runs every <see cref="Cadence"/> whenever the broker is connected
/// and there is at least one non-terminal local order.
///
/// For each matching local order (matched by BrokerId), updates:
///   * Status               → from broker status (Pending/Open/Executed/Rejected/Cancelled)
///   * ExecutedPrice        → averagePrice
///   * FilledQuantity       → filledShares
///   * ExecutedAt           → set when a Pending order transitions to Executed
///   * ErrorMessage / Text  → broker rejection/status message
///
/// Emits <c>OrderStatusChanged</c> on the SignalR hub whenever anything changes
/// so the Orders grid updates live.
/// </summary>
public sealed class OrderSyncService : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(5);

    private readonly ILogger<OrderSyncService> _logger;
    private readonly IServiceProvider _services;
    private readonly IHubContext<TradingHub> _hub;

    public OrderSyncService(
        ILogger<OrderSyncService> logger,
        IServiceProvider services,
        IHubContext<TradingHub> hub)
    {
        _logger = logger;
        _services = services;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Order sync service started (cadence {Seconds}s)", Cadence.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Order sync tick failed, continuing");
            }

            try { await Task.Delay(Cadence, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Order sync service stopped");
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var broker = scope.ServiceProvider.GetRequiredService<IBroker>();
        if (!broker.IsConnected)
            return;

        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        // Only pull orders that are still open at our end. If everything local
        // is terminal, no need to query the broker.
        var localOpen = await db.Orders
            .Where(o =>
                !string.IsNullOrEmpty(o.BrokerId) &&
                o.Status != OrderStatus.Executed &&
                o.Status != OrderStatus.Rejected &&
                o.Status != OrderStatus.Cancelled &&
                o.Status != OrderStatus.Failed)
            .ToListAsync(ct);

        if (localOpen.Count == 0)
            return;

        List<BrokerOrder> book;
        try
        {
            book = await broker.GetOrderBookAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetOrderBookAsync failed during sync");
            return;
        }

        if (book.Count == 0)
            return;

        var byId = book
            .Where(b => !string.IsNullOrWhiteSpace(b.OrderId))
            .GroupBy(b => b.OrderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var changed = new List<Order>();

        foreach (var local in localOpen)
        {
            if (!byId.TryGetValue(local.BrokerId, out var remote))
                continue;

            var mutated = false;

            if (local.Status != remote.Status)
            {
                _logger.LogInformation(
                    "Order {BrokerId} status change: {Old} → {New} ({Text})",
                    local.BrokerId, local.Status, remote.Status, remote.Text);
                local.Status = remote.Status;
                mutated = true;

                if (remote.Status == OrderStatus.Executed && local.ExecutedAt is null)
                    local.ExecutedAt = DateTime.UtcNow;
            }

            if (remote.AveragePrice.HasValue && remote.AveragePrice.Value > 0 &&
                local.ExecutedPrice != remote.AveragePrice)
            {
                local.ExecutedPrice = remote.AveragePrice;
                mutated = true;
            }

            if (remote.FilledQuantity.HasValue &&
                local.FilledQuantity != remote.FilledQuantity)
            {
                local.FilledQuantity = remote.FilledQuantity;
                mutated = true;
            }

            if (!string.IsNullOrWhiteSpace(remote.Text) && local.ErrorMessage != remote.Text)
            {
                local.ErrorMessage = remote.Text;
                mutated = true;
            }

            if (mutated)
                changed.Add(local);
        }

        if (changed.Count == 0)
            return;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Order sync: reconciled {Count} order(s) from broker terminal", changed.Count);

        try
        {
            await _hub.Clients.All.SendAsync("OrderStatusChanged",
                changed.Select(o => new
                {
                    o.Id,
                    o.BrokerId,
                    Status = o.Status.ToString(),
                    o.ExecutedPrice,
                    o.FilledQuantity,
                    o.ErrorMessage,
                    Timestamp = DateTime.UtcNow
                }),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast OrderStatusChanged");
        }
    }
}
