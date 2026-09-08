using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Helpers;
using NexusApp.Hubs;
using NexusApp.Interfaces;
using NexusApp.Models;
using System.Linq;

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
public sealed class OrderSyncService(
    ILogger<OrderSyncService> logger,
    IServiceProvider services,
    IHubContext<TradingHub> hub) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(5);

    private readonly ILogger<OrderSyncService> _logger = logger;
    private readonly IServiceProvider _services = services;
    private readonly IHubContext<TradingHub> _hub = hub;

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

    /// <summary>
    /// Cancels real broker orders that have been in Pending/Accepted state for
    /// longer than the configurable <c>PendingOrderTimeoutMinutes</c> setting.
    /// Skips paper-account orders and failure-stub orders (BrokerId starting
    /// with "REJ-" or "Paper").
    /// </summary>
    private async Task CancelStaleOrdersAsync(TradingDbContext db, ISettingsService settings, CancellationToken ct)
    {
        var timeoutMinutes = await settings.GetSettingAsync<int?>("PendingOrderTimeoutMinutes") ?? 5;
        if (timeoutMinutes <= 0)
            return; // Feature disabled

        var cutoff = DateTime.UtcNow.AddMinutes(-timeoutMinutes);

        var staleOrders = await db.Orders
            .Include(o => o.TradingAccount)
            .Where(o =>
                !string.IsNullOrEmpty(o.BrokerId) &&
                (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Accepted) &&
                o.CreatedAt < cutoff)
            .ToListAsync(ct);

        // Filter out paper / failure-stub orders in memory.
        // Resting broker-side target orders (Sell LIMIT / MIS) are deliberately long-lived —
        // they must survive until the target fills or an exit cancels them, so the stale
        // timeout must never touch them.
        staleOrders = [.. staleOrders
            .Where(o => !o.BrokerId.StartsWith("REJ-", StringComparison.OrdinalIgnoreCase) &&
                        !o.BrokerId.StartsWith("Paper", StringComparison.OrdinalIgnoreCase) &&
                        !RestingTargetOrders.IsRestingTarget(o))];
        if (staleOrders.Count == 0)
            return;

        _logger.LogInformation(
            "Found {Count} stale pending order(s) older than {Timeout} min — initiating auto-cancel",
            staleOrders.Count, timeoutMinutes);

        var cancelled = new List<Order>();
        var notifications = _services.CreateScope().ServiceProvider.GetService<INotificationService>();

        foreach (var order in staleOrders)
        {
            try
            {
                var brokerType = order.TradingAccount?.BrokerType ?? "AngelOne";
                using var brokerScope = _services.CreateScope();
                var broker = brokerScope.ServiceProvider.GetRequiredKeyedService<IBroker>(brokerType);

                if (!broker.IsConnected)
                {
                    _logger.LogDebug("Broker {BrokerType} not connected, skipping auto-cancel for {BrokerId}",
                        brokerType, order.BrokerId);
                    continue;
                }

                var ok = await broker.CancelOrderAsync(order.BrokerId);
                if (ok)
                {
                    var age = (DateTime.UtcNow - order.CreatedAt).TotalMinutes;
                    order.Status = OrderStatus.Cancelled;
                    order.ErrorMessage = $"Auto-cancelled: pending for {age:0.0} min (limit: {timeoutMinutes} min)";
                    cancelled.Add(order);

                    _logger.LogInformation(
                        "Auto-cancelled stale order {BrokerId} for {Symbol} (age: {Age:0.0} min)",
                        order.BrokerId, order.Symbol, age);
                }
                else
                {
                    _logger.LogWarning(
                        "Broker returned false for cancel of stale order {BrokerId} — may already be terminal",
                        order.BrokerId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-cancel stale order {BrokerId}", order.BrokerId);
            }
        }

        if (cancelled.Count == 0)
            return;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Auto-cancelled {Count} stale pending order(s)", cancelled.Count);

        // Notify via Telegram & SignalR
        if (notifications is not null)
        {
            foreach (var order in cancelled)
            {
                await notifications.SendOrderNotificationAsync(
                                order.Symbol,
                                "AUTO-CANCELLED",
                                $"Order {order.BrokerId} auto-cancelled after pending for >{timeoutMinutes} min");

                var tgMsg = $@"⏰ ORDER AUTO-CANCELLED (TIMEOUT)

• Symbol: {order.Symbol}
• Order ID: {order.BrokerId}
• Status: CANCELLED (pending >{timeoutMinutes} min)
• Reason: {order.ErrorMessage}
• Time: {DateTime.UtcNow.ToIstString("hh:mm:ss tt")} IST";

                    await notifications.SendTelegramCustomNotificationAsync(tgMsg);
                }
            }

            try
        {
            await _hub.Clients.All.SendAsync("OrderStatusChanged",
                cancelled.Select(o => new
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
            _logger.LogDebug(ex, "Failed to broadcast OrderStatusChanged for auto-cancelled orders");
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var broker = scope.ServiceProvider.GetRequiredService<IBroker>();
        if (!broker.IsConnected)
            return;

        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        // Auto-cancel stale pending orders before reconciling with broker book
        await CancelStaleOrdersAsync(db, settings, ct);

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

        // Maintain locally-tracked live positions for orders that just executed.
        // This enables app-side SL/target monitoring for non-Robo (Limit) orders
        // where the broker is NOT managing brackets.
        await ReconcileLivePositionsAsync(db, changed, ct);

        var notifications = scope.ServiceProvider.GetService<INotificationService>();

        foreach (var order in changed)
        {
            if (notifications is null) break;

            if (order.Status == OrderStatus.Executed)
            {
                await notifications.SendTradeSoundAsync(
                    order.Side == OrderSide.Sell ? "live-exit" : "live-entry");

                var priceDisplay = order.ExecutedPrice > 0 ? order.ExecutedPrice : order.Price;
                await notifications.SendOrderNotificationAsync(
                    order.Symbol,
                    "EXECUTED",
                    $"Order {order.BrokerId} executed on Angel One terminal @ ₹{priceDisplay:N2}");

                var tgMsg = $@"✅ ORDER EXECUTED ON TERMINAL ⚡

• Symbol: {order.Symbol}
• Order ID: {order.BrokerId}
• Executed Price: ₹{priceDisplay:N2}
• Status: EXECUTED (Filled on Angel Terminal)
• Time: {DateTime.UtcNow.ToIstString("hh:mm:ss tt")} IST";

                await notifications.SendTelegramCustomNotificationAsync(tgMsg);
            }
            else if (order.Status == OrderStatus.Rejected || order.Status == OrderStatus.Cancelled)
            {
                var statusStr = order.Status.ToString().ToUpper();
                await notifications.SendOrderNotificationAsync(
                    order.Symbol,
                    statusStr,
                    $"Order {order.BrokerId} {statusStr} on Angel One: {order.ErrorMessage}");

                var tgMsg = $@"❌ ORDER {statusStr} ON TERMINAL ⚡

• Symbol: {order.Symbol}
• Order ID: {order.BrokerId}
• Status: {statusStr}
• Reason: {order.ErrorMessage}
• Time: {DateTime.UtcNow.ToIstString("hh:mm:ss tt")} IST";

                await notifications.SendTelegramCustomNotificationAsync(tgMsg);
            }
        }

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
            await _hub.Clients.All.SendAsync("PositionChanged", new { Timestamp = DateTime.UtcNow }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast OrderStatusChanged");
        }
    }

    /// <summary>
    /// Creates or closes locally-tracked live positions in response to broker fills.
    ///
    /// All <b>live</b> account fills are tracked (paper positions are simulated by
    /// <see cref="PaperTradingEngine"/> and are excluded), so the dashboard can report
    /// live open positions and MTM:
    ///
    ///   * BUY  fill → opens a position carrying the signal's StopLoss/Targets.
    ///                 Non-Robo (Limit / MIS) entries are flagged
    ///                 <see cref="Position.ManagedLocally"/> so the price monitor can
    ///                 square them off on SL/target; Robo/bracket entries
    ///                 (<see cref="ProductType.Mos"/>) are tracked for reporting only
    ///                 because the broker owns their exits.
    ///   * SELL fill → closes the matching open position (exit already happened at broker).
    /// </summary>
    private async Task ReconcileLivePositionsAsync(TradingDbContext db, List<Order> changed, CancellationToken ct)
    {
        var executed = changed
            .Where(o => o.Status == OrderStatus.Executed &&
                        !string.IsNullOrEmpty(o.BrokerId) &&
                        !o.BrokerId.StartsWith("Paper", StringComparison.OrdinalIgnoreCase) &&
                        !o.BrokerId.StartsWith("REJ-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (executed.Count == 0)
            return;

        var mutated = false;

        foreach (var order in executed)
        {
            try
            {
                var fillPrice = order.ExecutedPrice is > 0 ? order.ExecutedPrice.Value : order.Price;

                if (order.Side == OrderSide.Sell)
                {
                    // Closing fill — mark the matching open position closed (broker already exited).
                    var open = await db.Positions.FirstOrDefaultAsync(p =>
                        p.TradingAccountId == order.TradingAccountId &&
                        p.Symbol == order.Symbol &&
                        p.ClosedAt == null, ct);

                    if (open is not null)
                    {
                        open.ClosedAt = DateTime.UtcNow;
                        open.ClosingPrice = fillPrice;
                        open.CurrentPrice = fillPrice;
                        open.RealizedPnL = PnlCalculator.RealizedPnl(open.EntryPrice, fillPrice, open.Quantity);
                        open.UnrealizedPnL = 0;
                        open.UnrealizedPnLPercentage = 0;
                        mutated = true;
                        _logger.LogInformation("Live position closed via Sell fill: {Symbol} @ {Price}", open.Symbol, fillPrice);

                        // Release the live price feed for this symbol — it is no longer
                        // monitored, so we don't want its WS subscription lingering.
                        await ReleaseSymbolFeedSafeAsync(open.TradingAccountId, open.Symbol, db, ct);
                    }
                }
                else
                {
                    // Opening BUY fill — track the position if one isn't tracked yet.
                    // Robo/bracket (Mos) fills are tracked for reporting only; their
                    // SL/target exits stay with the broker.
                    var isBrokerManaged = order.ProductType == ProductType.Mos;

                    var already = await db.Positions.AnyAsync(p =>
                        p.TradingAccountId == order.TradingAccountId &&
                        p.Symbol == order.Symbol &&
                        p.ClosedAt == null, ct);

                    if (already)
                        continue;

                    var signal = await db.TradingSignals.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == order.SignalId, ct);

                    var qty = order.FilledQuantity is > 0 ? order.FilledQuantity.Value : order.Quantity;
                    var slValue = signal is { StopLoss: > 0 } ? signal.StopLoss : (decimal?)null;
                    var targetList = signal?.Targets.ToList() ?? [];

                    db.Positions.Add(new Position
                    {
                        TradingAccountId = order.TradingAccountId,
                        SignalId = order.SignalId,
                        Symbol = order.Symbol,
                        Quantity = qty,
                        EntryPrice = fillPrice,
                        CurrentPrice = fillPrice,
                        StopLoss = slValue,
                        Targets = targetList,
                        OpenedAt = order.ExecutedAt ?? DateTime.UtcNow,
                        UnrealizedPnL = 0,
                        UnrealizedPnLPercentage = 0,
                        ManagedLocally = !isBrokerManaged
                    });
                    mutated = true;
                    _logger.LogInformation(
                        "Tracking live position ({Mode}): {Symbol} @ {Price} (SL={SL}, Targets={Targets})",
                        isBrokerManaged ? "broker-managed Robo/BO, reporting only" : "app-managed SL/target",
                        order.Symbol, fillPrice, slValue?.ToString() ?? "None",
                        string.Join("/", targetList));

                    if (!isBrokerManaged)
                        await PlaceRestingTargetOrderAsync(db, order, qty, fillPrice, targetList, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile live position for order {BrokerId}", order.BrokerId);
            }
        }

        if (mutated)
        {
            await db.SaveChangesAsync(ct);
            try { await _hub.Clients.All.SendAsync("PositionChanged", new { Timestamp = DateTime.UtcNow }, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to broadcast PositionChanged after live reconcile"); }
        }
    }

    /// <summary>
    /// After a plain LIMIT entry fills, rests a SELL LIMIT order at the broker for the
    /// nearest profitable target so the profit exit survives an app/network outage.
    /// Only runs when <c>OrderVariety</c> is <c>Limit</c> (Robo orders already carry a
    /// broker-side target, Market orders opt out). Never throws — a failure here simply
    /// leaves the position under app-side target monitoring.
    /// </summary>
    private async Task PlaceRestingTargetOrderAsync(
        TradingDbContext db, Order order, decimal qty, decimal fillPrice, List<decimal> targetList, CancellationToken ct)
    {
        try
        {
            if (qty <= 0 || targetList.Count == 0)
                return;

            using var scope = _services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

            // A Robo order carries its own broker-side target, so it needs no resting
            // order; Market orders opt out. The order's ProductType is the reliable
            // signal here rather than the global setting: MCX and BSE F&O orders are
            // downgraded from Robo to a plain Limit at placement time (bracket orders
            // are blocked on those exchanges), and those downgraded orders do need a
            // resting target even though OrderVariety still reads "Robo".
            var variety = await settings.GetSettingAsync<string>("OrderVariety") ?? string.Empty;
            var isMarket = string.Equals(variety, "Market", StringComparison.OrdinalIgnoreCase);
            if (isMarket || order.ProductType == ProductType.Mos)
                return;

            // Only targets above the fill make sense for a long exit; take the nearest one.
            var target = targetList.Where(t => t > fillPrice).DefaultIfEmpty(0m).Min();
            if (target <= 0)
            {
                _logger.LogDebug("No target above fill {Fill} for {Symbol}; skipping resting target order",
                    fillPrice, order.Symbol);
                return;
            }

            var brokerType = await db.TradingAccounts.AsNoTracking()
                .Where(a => a.Id == order.TradingAccountId)
                .Select(a => a.BrokerType)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(brokerType))
                return;

            var broker = scope.ServiceProvider.GetRequiredKeyedService<IBroker>(brokerType);
            if (!broker.IsConnected)
            {
                _logger.LogWarning("Broker {Broker} not connected; resting target order skipped for {Symbol}",
                    brokerType, order.Symbol);
                return;
            }

            string? symbolToken = null;
            string? exchange = null;
            try
            {
                var instrumentMaster = scope.ServiceProvider.GetService<NexusApp.Brokers.AngelOne.AngelInstrumentMaster>();
                if (instrumentMaster is not null)
                {
                    await instrumentMaster.EnsureLoadedAsync(ct);
                    var entry = instrumentMaster.FindByTradingSymbol(order.Symbol);
                    if (entry is not null)
                    {
                        symbolToken = entry.Token;
                        exchange = entry.ExchangeSegment;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Instrument master lookup failed for resting target {Symbol}", order.Symbol);
            }

            var response = await broker.PlaceOrderAsync(new BrokerOrderRequest
            {
                Symbol = order.Symbol,
                SymbolToken = symbolToken,
                Exchange = exchange,
                Quantity = qty,
                Price = target,
                Side = OrderSide.Sell,
                OrderType = OrderType.Limit,
                ProductType = ProductType.Mis
            });

            db.Orders.Add(new Order
            {
                SignalId = order.SignalId,
                TradingAccountId = order.TradingAccountId,
                Symbol = order.Symbol,
                Quantity = qty,
                Price = target,
                Side = OrderSide.Sell,
                OrderType = OrderType.Limit,
                ProductType = ProductType.Mis,
                Status = response.Success ? OrderStatus.Accepted : OrderStatus.Failed,
                BrokerId = response.OrderId,
                ErrorMessage = response.ErrorMessage,
                CreatedAt = DateTime.UtcNow
            });

            if (response.Success)
            {
                _logger.LogInformation("Resting target SELL LIMIT placed for {Symbol}: {Qty} @ {Target} (broker id {BrokerId})",
                    order.Symbol, qty, target, response.OrderId);
            }
            else
            {
                _logger.LogWarning("Resting target order rejected for {Symbol}: {Error}",
                    order.Symbol, response.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to place resting target order for {Symbol}", order.Symbol);
        }
    }

    /// <summary>
    /// Best-effort release of a symbol's live price feed (WebSocket subscription) once its
    /// position is closed. Resolves the position's account broker type and asks that broker
    /// to drop the feed. Never throws — feed cleanup must not break reconciliation.
    /// </summary>
    private async Task ReleaseSymbolFeedSafeAsync(int accountId, string symbol, TradingDbContext db, CancellationToken ct)
    {
        try
        {
            var brokerType = await db.TradingAccounts.AsNoTracking()
                .Where(a => a.Id == accountId)
                .Select(a => a.BrokerType)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(brokerType))
                return;

            using var scope = _services.CreateScope();
            var broker = scope.ServiceProvider.GetRequiredKeyedService<IBroker>(brokerType);
            await broker.ReleaseSymbolFeedAsync(symbol);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to release live feed for {Symbol}", symbol);
        }
    }
}
