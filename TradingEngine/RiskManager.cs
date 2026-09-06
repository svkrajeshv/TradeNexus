using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Models;
using NexusApp.Helpers;
using NexusApp.Interfaces;
using NexusApp.Hubs;

namespace NexusApp.TradingEngine;

/// <summary>
/// Risk management engine for validating trading limits
/// </summary>
public class RiskManager(TradingDbContext context, ISettingsService settings, ILogger<RiskManager> logger)
{
    private const string AccountReasonLogTemplate = "Account {Name}: {Reason}";

    private readonly TradingDbContext _context = context;
    private readonly ISettingsService _settings = settings;
    private readonly ILogger<RiskManager> _logger = logger;

    /// <summary>
    /// Validates if account meets all risk criteria (both global portfolio limits and account-specific limits)
    /// </summary>
    public async Task<(bool IsValid, string Reason)> ValidateLimitsAsync(TradingAccount account)
    {
        try
        {
            // Tier 2: Check global portfolio limits first (combined live + paper)
            var (globalValid, globalReason) = await ValidateGlobalLimitsAsync();
            if (!globalValid)
            {
                _logger.LogWarning("Account {Name} blocked by global limit: {Reason}", account.Name, globalReason);
                return (false, globalReason);
            }

            var todayIst = DateTime.UtcNow.ToIst().Date;
            var startOfDayUtc = todayIst.AddHours(-5.5);
            var endOfDayUtc = startOfDayUtc.AddDays(1);

            // Tier 1: Account-specific limits (configured per account on Accounts page)
            var dailyMaxLoss = account.DailyMaxLoss;
            var dailyMaxProfit = account.DailyMaxProfit;
            var maxOpenPositions = account.MaxOpenPositions;
            var maxTradesPerDay = account.MaxTradesPerDay;

            // Calculate actual Daily P&L for THIS account from closed and open positions today (IST)
            var closedPositionsToday = await _context.Positions
                .AsNoTracking()
                .Where(p => p.TradingAccountId == account.Id
                    && p.ClosedAt.HasValue
                    && p.ClosedAt.Value >= startOfDayUtc
                    && p.ClosedAt.Value < endOfDayUtc)
                .ToListAsync();

            var openPositionsList = await _context.Positions
                .AsNoTracking()
                .Where(p => p.TradingAccountId == account.Id && p.ClosedAt == null)
                .ToListAsync();

            decimal realizedPnL = closedPositionsToday.Sum(p => p.RealizedPnL ?? 0m);
            decimal unrealizedPnL = openPositionsList.Sum(p => p.UnrealizedPnL);
            decimal dailyPnL = realizedPnL + unrealizedPnL;

            // Check account daily loss limit
            if (dailyMaxLoss > 0 && dailyPnL < -dailyMaxLoss)
            {
                var reason = $"Account daily max loss limit reached (P&L: ₹{dailyPnL:N2}, Limit: ₹{dailyMaxLoss:N2})";
                _logger.LogWarning(AccountReasonLogTemplate, account.Name, reason);
                return (false, reason);
            }

            // Check account daily profit limit
            if (dailyMaxProfit > 0 && dailyPnL > dailyMaxProfit)
            {
                var reason = $"Account daily max profit target reached (P&L: ₹{dailyPnL:N2}, Limit: ₹{dailyMaxProfit:N2})";
                _logger.LogWarning(AccountReasonLogTemplate, account.Name, reason);
                return (false, reason);
            }

            // Check max trades per day (executed or accepted orders today in IST)
            var todayOrdersCount = await _context.Orders
                .AsNoTracking()
                .Where(o => o.TradingAccountId == account.Id
                    && o.CreatedAt >= startOfDayUtc
                    && o.CreatedAt < endOfDayUtc
                    && (o.Status == OrderStatus.Executed || o.Status == OrderStatus.Accepted))
                .CountAsync();

            if (maxTradesPerDay > 0 && todayOrdersCount >= maxTradesPerDay)
            {
                var reason = $"Max trades per day limit reached ({todayOrdersCount}/{maxTradesPerDay} trades)";
                _logger.LogWarning(AccountReasonLogTemplate, account.Name, reason);
                return (false, reason);
            }

            // Check max open positions
            int openPositionsCount = openPositionsList.Count;
            if (maxOpenPositions > 0 && openPositionsCount >= maxOpenPositions)
            {
                var reason = $"Max open positions limit reached ({openPositionsCount}/{maxOpenPositions} positions)";
                _logger.LogWarning(AccountReasonLogTemplate, account.Name, reason);
                return (false, reason);
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating risk limits for account {Name}", account.Name);
            return (false, $"Risk check error: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates a specific order against risk rules
    /// </summary>
    public async Task<bool> ValidateOrderAsync(Order order, TradingAccount account)
    {
        try
        {
            // Quantity guard: order.Quantity is raw shares (lots × lotSize).
            // Use a generous upper-bound — 50 lots × largest lot-size (120 for MIDCPNIFTY) = 6000.
            // This prevents fat-finger errors while not blocking normal trades.
            const decimal maxRawQty = 10_000m;
            if (order.Quantity > maxRawQty)
            {
                _logger.LogWarning("Order quantity suspiciously large: {Quantity} — rejected", order.Quantity);
                order.ErrorMessage = $"Quantity limit exceeded (qty={order.Quantity}). Max limit is {maxRawQty}.";
                return false;
            }

            var allowAfterHours = await _context.ApplicationSettings
                .AsNoTracking()
                .Where(s => s.Key == "AllowAfterMarketHours")
                .Select(s => s.Value)
                .FirstOrDefaultAsync();
            var bypassMarketHours = string.Equals(allowAfterHours, "true", StringComparison.OrdinalIgnoreCase);

            var killSwitchVal = await _context.ApplicationSettings
                .AsNoTracking()
                .Where(s => s.Key == "KillSwitch")
                .Select(s => s.Value)
                .FirstOrDefaultAsync();
            var isKillSwitchEnabled = string.Equals(killSwitchVal, "true", StringComparison.OrdinalIgnoreCase);

            if (isKillSwitchEnabled)
            {
                _logger.LogWarning("Emergency Kill Switch is ACTIVE — blocking order execution.");
                order.ErrorMessage = "Blocked by Emergency Kill Switch. Disable it in Settings to trade.";
                return false;
            }

            // Check trading hours unless bypass is enabled. The window depends on the
            // segment: NSE/BSE 09:15-15:30, MCX 09:00-23:30.
            var now = DateTime.UtcNow.ToIst();
            var segment = MarketSegments.ForTradingSymbol(order.Symbol);

            if (segment == MarketSegment.Commodity)
            {
                // Commodity orders are blocked outright unless the master toggle is on.
                // This is the authoritative gate: the parser gate alone would not stop an
                // already-parsed signal or a manually-entered commodity order.
                var mcxEnabled = await _settings.GetSettingAsync<bool?>("Mcx.Enabled") ?? false;
                if (!mcxEnabled)
                {
                    _logger.LogWarning("Commodity trading is disabled - blocking MCX order for {Symbol}.", order.Symbol);
                    order.ErrorMessage = "Commodity (MCX) trading is disabled. Enable it in Settings \u2192 MCX to trade commodities.";
                    return false;
                }
            }

            var marketOpen = segment == MarketSegment.Commodity ? MarketHours.CommodityOpen : MarketHours.Open;
            var marketClose = segment == MarketSegment.Commodity ? MarketHours.CommodityClose : MarketHours.Close;

            if (!bypassMarketHours && (now.TimeOfDay < marketOpen || now.TimeOfDay > marketClose))
            {
                if (segment == MarketSegment.Commodity)
                {
                    _logger.LogWarning("Trading outside MCX market hours ({Time} IST). Market: 09:00\u201323:30", now.ToString("HH:mm"));
                    order.ErrorMessage = $"MCX market is closed (current time: {now:hh:mm tt} IST). MCX hours are 09:00 AM to 11:30 PM. Enable 'Allow orders after market hours' in Settings to test.";
                    return false;
                }

                _logger.LogWarning("Trading outside market hours ({Time} IST). Market: 09:15\u201315:30", now.ToString("HH:mm"));
                order.ErrorMessage = $"Market is closed (current time: {now:hh:mm tt} IST). Market hours are 09:15 AM to 03:30 PM. Enable 'Allow orders after market hours' in Settings to test.";
                return false;
            }

            if (!account.IsEnabled)
            {
                _logger.LogWarning("Account {Id} is disabled", account.Id);
                order.ErrorMessage = $"Trading account '{account.Name}' is disabled. Enable it in the Accounts screen.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating order");
            return false;
        }
    }

    /// <summary>
    /// Gets current account risk metrics
    /// </summary>
    public async Task<RiskMetrics> GetRiskMetricsAsync(int accountId)
    {
        try
        {
            var account = await _context.TradingAccounts.FindAsync(accountId);
            if (account == null)
                return new();

            var todayIst = DateTime.UtcNow.ToIst().Date;
            var startOfDayUtc = todayIst.AddHours(-5.5);
            var endOfDayUtc = startOfDayUtc.AddDays(1);

            // Tier 1: Per-account metrics
            var dailyMaxLoss = account.DailyMaxLoss;
            var dailyMaxProfit = account.DailyMaxProfit;
            var maxOpenPositions = account.MaxOpenPositions;
            var maxTradesPerDay = account.MaxTradesPerDay;

            var todayOrders = await _context.Orders
                .Where(o => o.TradingAccountId == accountId
                    && o.CreatedAt >= startOfDayUtc
                    && o.CreatedAt < endOfDayUtc
                    && (o.Status == OrderStatus.Executed || o.Status == OrderStatus.Accepted))
                .CountAsync();

            var openPositionsList = await _context.Positions
                .Where(p => p.TradingAccountId == accountId && p.ClosedAt == null)
                .ToListAsync();

            var closedPositionsToday = await _context.Positions
                .Where(p => p.TradingAccountId == accountId
                    && p.ClosedAt != null
                    && p.ClosedAt >= startOfDayUtc
                    && p.ClosedAt < endOfDayUtc)
                .ToListAsync();

            decimal realizedPnL = closedPositionsToday.Sum(p => p.RealizedPnL ?? 0m);
            decimal unrealizedPnL = openPositionsList.Sum(p => p.UnrealizedPnL);
            decimal dailyPnL = realizedPnL + unrealizedPnL;

            return new RiskMetrics
            {
                TradesPerDayUsed = todayOrders,
                MaxTradesPerDay = maxTradesPerDay,
                OpenPositionsCount = openPositionsList.Count,
                MaxOpenPositions = maxOpenPositions,
                DailyPnL = dailyPnL,
                DailyMaxLoss = dailyMaxLoss,
                DailyMaxProfit = dailyMaxProfit
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting risk metrics");
            return new();
        }
    }

    /// <summary>
    /// Validates global portfolio limits across ALL accounts (live + paper combined)
    /// </summary>
    public async Task<(bool IsValid, string Reason)> ValidateGlobalLimitsAsync()
    {
        try
        {
            var metrics = await GetGlobalPortfolioRiskMetricsAsync();

            if (metrics.DailyMaxLoss > 0 && metrics.DailyPnL <= -metrics.DailyMaxLoss)
            {
                var reason = $"Global portfolio daily max loss reached (Combined P&L: ₹{metrics.DailyPnL:N2}, Limit: ₹{metrics.DailyMaxLoss:N2})";
                return (false, reason);
            }

            if (metrics.DailyMaxProfit > 0 && metrics.DailyPnL >= metrics.DailyMaxProfit)
            {
                var reason = $"Global portfolio daily max profit reached (Combined P&L: ₹{metrics.DailyPnL:N2}, Limit: ₹{metrics.DailyMaxProfit:N2})";
                return (false, reason);
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating global portfolio risk limits");
            return (false, $"Global risk check error: {ex.Message}");
        }
    }

    /// <summary>
    /// Computes portfolio-wide risk metrics across all accounts (both live and paper combined).
    /// </summary>
    public async Task<GlobalRiskMetrics> GetGlobalPortfolioRiskMetricsAsync()
    {
        try
        {
            var todayIst = DateTime.UtcNow.ToIst().Date;
            var startOfDayUtc = todayIst.AddHours(-5.5);
            var endOfDayUtc = startOfDayUtc.AddDays(1);

            var globalMaxLoss = await _settings.GetSettingAsync<decimal?>("DailyMaxLoss") ?? 0m;
            var globalMaxProfit = await _settings.GetSettingAsync<decimal?>("DailyMaxProfit") ?? 0m;

            // Aggregate closed P&L today across all accounts (live + paper)
            var closedRealized = await _context.Positions
                .AsNoTracking()
                .Where(p => p.ClosedAt.HasValue
                    && p.ClosedAt.Value >= startOfDayUtc
                    && p.ClosedAt.Value < endOfDayUtc)
                .SumAsync(p => p.RealizedPnL ?? 0m);

            // Aggregate open unrealized P&L across all accounts (live + paper)
            var openUnrealized = await _context.Positions
                .AsNoTracking()
                .Where(p => p.ClosedAt == null)
                .SumAsync(p => p.UnrealizedPnL);

            var combinedPnL = closedRealized + openUnrealized;

            var openPositionsCount = await _context.Positions
                .AsNoTracking()
                .Where(p => p.ClosedAt == null)
                .CountAsync();

            return new GlobalRiskMetrics
            {
                DailyPnL = combinedPnL,
                DailyMaxLoss = globalMaxLoss,
                DailyMaxProfit = globalMaxProfit,
                OpenPositionsCount = openPositionsCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing global portfolio risk metrics");
            return new();
        }
    }
}

/// <summary>
/// Risk metrics DTO
/// </summary>
public class RiskMetrics
{
    public int TradesPerDayUsed { get; set; }
    public int MaxTradesPerDay { get; set; }
    public int OpenPositionsCount { get; set; }
    public int MaxOpenPositions { get; set; }
    public decimal DailyPnL { get; set; }
    public decimal DailyMaxLoss { get; set; }
    public decimal DailyMaxProfit { get; set; }

    public bool IsWithinLimits => 
        TradesPerDayUsed < MaxTradesPerDay &&
        OpenPositionsCount < MaxOpenPositions &&
        DailyPnL >= -DailyMaxLoss &&
        DailyPnL <= DailyMaxProfit;
}

/// <summary>
/// Portfolio-wide risk metrics across all accounts (live + paper)
/// </summary>
public class GlobalRiskMetrics
{
    public decimal DailyPnL { get; set; }
    public decimal DailyMaxLoss { get; set; }
    public decimal DailyMaxProfit { get; set; }
    public int OpenPositionsCount { get; set; }

    public bool IsWithinLimits =>
        (DailyMaxLoss <= 0 || DailyPnL >= -DailyMaxLoss) &&
        (DailyMaxProfit <= 0 || DailyPnL <= DailyMaxProfit);
}

/// <summary>
/// Paper trading engine for simulated trading
/// </summary>
public class PaperTradingEngine(
    TradingDbContext context,
    INotificationService notifications,
    ILogger<PaperTradingEngine> logger,
    IHubContext<TradingHub> hub,
    ISettingsService settings)
{
    private const string PaperTradingAccountName = "Paper Trading Account";
    private const string PositionChangedEvent = "PositionChanged";
    private const string OrderStatusChangedEvent = "OrderStatusChanged";

    private readonly TradingDbContext _context = context;
    private readonly INotificationService _notifications = notifications;
    private readonly ILogger<PaperTradingEngine> _logger = logger;
    private readonly IHubContext<TradingHub> _hub = hub;
    private readonly ISettingsService _settings = settings;

    /// <summary>
    /// Simulates order execution without broker
    /// </summary>
    public async Task<bool> SimulateOrderAsync(Order order, decimal currentPrice)
    {
        try
        {
            if (order.Side == OrderSide.Sell)
            {
                var openPosition = await _context.Positions
                    .Include(p => p.Signal)
                    .FirstOrDefaultAsync(p => p.TradingAccountId == order.TradingAccountId
                        && p.Symbol == order.Symbol
                        && p.ClosedAt == null);

                if (openPosition is not null)
                {
                    // Direction-aware: a raw (exit - entry) formula reports a short's loss
                    // as a profit. Every other close path routes through PnlCalculator.
                    var isLong = openPosition.Signal is null || openPosition.Signal.Action == SignalAction.Buy;

                    openPosition.ClosedAt = DateTime.UtcNow;
                    openPosition.ClosingPrice = currentPrice;
                    openPosition.RealizedPnL = PnlCalculator.RealizedPnl(
                        openPosition.EntryPrice, currentPrice, openPosition.Quantity, isShort: !isLong);
                    openPosition.CurrentPrice = currentPrice;
                    openPosition.UnrealizedPnL = 0;
                    openPosition.UnrealizedPnLPercentage = 0;

                    order.Status = OrderStatus.Executed;
                    order.ExecutedAt = DateTime.UtcNow;
                    order.ExecutedPrice = currentPrice;
                    order.FilledQuantity = order.Quantity;

                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Paper position closed via Sell order: {Symbol} @ {Price}", openPosition.Symbol, currentPrice);

                    var acc = await _context.TradingAccounts.FindAsync(openPosition.TradingAccountId);
                    var accName = acc?.Name ?? PaperTradingAccountName;
                    await _notifications.SendTelegramOrderClosedAsync(accName, true, openPosition.Symbol, openPosition.EntryPrice, currentPrice, openPosition.RealizedPnL.Value, openPosition.Quantity, "Sell Order Executed");

                    await _hub.Clients.All.SendAsync(PositionChangedEvent, new { Timestamp = DateTime.UtcNow });
                    await _hub.Clients.All.SendAsync(OrderStatusChangedEvent, new { Timestamp = DateTime.UtcNow });
                    return true;
                }
            }

            order.Status = OrderStatus.Executed;
            order.ExecutedAt = DateTime.UtcNow;
            order.ExecutedPrice = currentPrice;
            order.FilledQuantity = order.Quantity;
            if (string.IsNullOrWhiteSpace(order.BrokerId) || order.BrokerId.StartsWith("PAPER-"))
            {
                order.BrokerId = "Paper Account";
            }

            // Fetch StopLoss and Targets from the signal
            var signal = await _context.TradingSignals.FindAsync(order.SignalId);
            var stopLoss = signal?.StopLoss;
            var targets = signal?.Targets.ToList() ?? [];

            // Create position
            var position = new Position
            {
                TradingAccountId = order.TradingAccountId,
                SignalId = order.SignalId,
                Symbol = order.Symbol,
                Quantity = order.Quantity,
                EntryPrice = currentPrice,
                CurrentPrice = currentPrice,
                StopLoss = stopLoss,
                Targets = targets,
                OpenedAt = DateTime.UtcNow,
                UnrealizedPnL = 0,
                UnrealizedPnLPercentage = 0
            };

            _context.Positions.Add(position);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Paper order simulated: {Symbol} @ {Price} (SL={SL}, Targets={Targets})", 
                order.Symbol, currentPrice, stopLoss?.ToString() ?? "None", string.Join("/", targets));
            await _hub.Clients.All.SendAsync(PositionChangedEvent, new { Timestamp = DateTime.UtcNow });
            await _hub.Clients.All.SendAsync(OrderStatusChangedEvent, new { Timestamp = DateTime.UtcNow });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error simulating paper order");
            return false;
        }
    }

    /// <summary>
    /// Updates paper positions with new prices
    /// </summary>
    public async Task UpdatePositionPricesAsync(Dictionary<string, decimal> prices)
    {
        try
        {
            var exitOffsetSetting = await _settings.GetSettingAsync<decimal?>("ExitPriceOffset");
            var exitOffset = exitOffsetSetting ?? 0m;

            // PAPER ONLY. This is the simulated exit engine: it invents SL/target fills
            // from a price feed. Live positions must never be closed here - their exits
            // belong to the broker and are reconciled from real fills by OrderSyncService.
            // Without this filter a live position gets a fabricated ClosedAt/RealizedPnL
            // from a simulated exit price, so the DB books a profit the terminal never
            // realised while the real broker leg is still running.
            var allOpen = await _context.Positions
                .Include(p => p.Signal)
                .Include(p => p.TradingAccount)
                .Where(p => p.ClosedAt == null)
                .ToListAsync();

            var positions = allOpen.Where(BookScope.IsPaper).ToList();

            foreach (var position in positions)
            {
                if (prices.TryGetValue(position.Symbol, out var newPrice))
                {
                    // Direction is authoritative from the originating signal's action.
                    // The app is long-only today; a BUY signal is a long position.
                    // Do NOT infer direction from the SL/entry relationship — SL/target
                    // levels can sit either side of entry and would misclassify the side,
                    // flipping the P&L sign (e.g. a long shown as a profit while at a loss).
                    bool isLong = position.Signal is null || position.Signal.Action == SignalAction.Buy;

                    position.CurrentPrice = newPrice;
                    position.UnrealizedPnL = PnlCalculator.UnrealizedPnl(position.EntryPrice, newPrice, position.Quantity, isShort: !isLong);
                    position.UnrealizedPnLPercentage = position.EntryPrice > 0
                        ? (position.UnrealizedPnL / (position.EntryPrice * position.Quantity)) * 100m
                        : 0m;

                    if (position.ManagedLocally)
                        continue;

                    var exitReason = ExitEvaluator.Evaluate(position, newPrice);
                    if (exitReason == ExitReason.StopLoss)
                    {
                            // Apply the simulated slippage to the observed price, not the
                            // trigger level. This preserves adverse gaps through an SL.
                            var slExitPrice = newPrice;
                            if (exitOffset > 0)
                            {
                                var adjustedPrice = isLong
                                    ? newPrice - exitOffset
                                    : newPrice + exitOffset;
                                slExitPrice = Math.Max(0.05m, adjustedPrice);
                            }

                            position.ClosedAt = DateTime.UtcNow;
                            position.ClosingPrice = slExitPrice;
                            position.RealizedPnL = PnlCalculator.RealizedPnl(position.EntryPrice, slExitPrice, position.Quantity, isShort: !isLong);
                            position.UnrealizedPnL = 0;
                            position.UnrealizedPnLPercentage = 0;
                            _logger.LogInformation("Paper position SL hit ({Side}): {Symbol} closed at {Price} (SL={SL}, Offset={Offset})",
                                isLong ? "Long" : "Short", position.Symbol, slExitPrice, position.StopLoss.GetValueOrDefault(), exitOffset);

                            var acc = await _context.TradingAccounts.FindAsync(position.TradingAccountId);
                            var accName = acc?.Name ?? PaperTradingAccountName;
                            await _notifications.SendTelegramOrderClosedAsync(accName, true, position.Symbol, position.EntryPrice, slExitPrice, position.RealizedPnL.Value, position.Quantity, "Stop Loss Hit");
                            continue;
                    }

                    if (exitReason == ExitReason.Target)
                    {
                        decimal hitTarget = isLong
                            ? position.Targets.Where(t => PriceComparison.Normalize(t) > PriceComparison.Normalize(position.EntryPrice))
                                              .OrderBy(t => t)
                                              .FirstOrDefault(t => PriceComparison.IsGreaterThanOrEqual(newPrice, t))
                            : position.Targets.Where(t => t > 0 && PriceComparison.Normalize(t) < PriceComparison.Normalize(position.EntryPrice))
                                              .OrderByDescending(t => t)
                                              .FirstOrDefault(t => PriceComparison.IsLessThanOrEqual(newPrice, t));
                        if (hitTarget > 0)
                        {
                            // Apply the simulated slippage to the observed price so price
                            // improvements or gaps are reflected by paper execution.
                            var targetExitPrice = newPrice;
                            if (exitOffset > 0)
                            {
                                var adjustedPrice = isLong
                                    ? newPrice - exitOffset
                                    : newPrice + exitOffset;
                                targetExitPrice = Math.Max(0.05m, adjustedPrice);
                            }

                            position.ClosedAt = DateTime.UtcNow;
                            position.ClosingPrice = targetExitPrice;
                            position.RealizedPnL = PnlCalculator.RealizedPnl(position.EntryPrice, targetExitPrice, position.Quantity, isShort: !isLong);
                            position.UnrealizedPnL = 0;
                            position.UnrealizedPnLPercentage = 0;
                            _logger.LogInformation("Paper position Target hit ({Side}): {Symbol} closed at {Price} (Target={Target}, Offset={Offset})",
                                isLong ? "Long" : "Short", position.Symbol, targetExitPrice, hitTarget, exitOffset);

                            var acc = await _context.TradingAccounts.FindAsync(position.TradingAccountId);
                            var accName = acc?.Name ?? PaperTradingAccountName;
                            await _notifications.SendTelegramOrderClosedAsync(accName, true, position.Symbol, position.EntryPrice, targetExitPrice, position.RealizedPnL.Value, position.Quantity, "Target Hit");
                        }
                    }
                }
            }

            await _context.SaveChangesAsync();
            await _hub.Clients.All.SendAsync(PositionChangedEvent, new { Timestamp = DateTime.UtcNow });
            await _hub.Clients.All.SendAsync(OrderStatusChangedEvent, new { Timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating position prices");
        }
    }

    /// <summary>
    /// Closes a paper position
    /// </summary>
    public async Task<bool> ClosePositionAsync(int positionId, decimal closingPrice)
    {
        try
        {
            var position = await _context.Positions
                .Include(p => p.Signal)
                .FirstOrDefaultAsync(p => p.Id == positionId);
            if (position == null)
                return false;

            var isLong = position.Signal is null || position.Signal.Action == SignalAction.Buy;

            position.ClosedAt = DateTime.UtcNow;
            position.ClosingPrice = closingPrice;
            position.RealizedPnL = PnlCalculator.RealizedPnl(
                position.EntryPrice, closingPrice, position.Quantity, isShort: !isLong);

            await _context.SaveChangesAsync();
            _logger.LogInformation("Paper position closed: {Symbol} @ {Price}", position.Symbol, closingPrice);

            var acc = await _context.TradingAccounts.FindAsync(position.TradingAccountId);
            var accName = acc?.Name ?? PaperTradingAccountName;
            await _notifications.SendTelegramOrderClosedAsync(accName, true, position.Symbol, position.EntryPrice, closingPrice, position.RealizedPnL.Value, position.Quantity, "Manual Close");

            await _hub.Clients.All.SendAsync(PositionChangedEvent, new { Timestamp = DateTime.UtcNow });
            await _hub.Clients.All.SendAsync(OrderStatusChangedEvent, new { Timestamp = DateTime.UtcNow });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing paper position");
            return false;
        }
    }

    /// <summary>
    /// Gets paper trading summary
    /// </summary>
    public async Task<PaperTradingSummary> GetSummaryAsync(int accountId)
    {
        try
        {
            var openPositions = await _context.Positions
                .Where(p => p.TradingAccountId == accountId && p.ClosedAt == null)
                .ToListAsync();

            var closedPositions = await _context.Positions
                .Where(p => p.TradingAccountId == accountId && p.ClosedAt != null)
                .ToListAsync();

            var totalUnrealizedPnL = openPositions.Sum(p => p.UnrealizedPnL);
            var totalRealizedPnL = closedPositions.Sum(p => p.RealizedPnL ?? 0);
            var totalPnL = totalUnrealizedPnL + totalRealizedPnL;

            return new PaperTradingSummary
            {
                OpenPositionsCount = openPositions.Count,
                ClosedPositionsCount = closedPositions.Count,
                TotalUnrealizedPnL = totalUnrealizedPnL,
                TotalRealizedPnL = totalRealizedPnL,
                TotalPnL = totalPnL,
                WinRate = closedPositions.Count > 0 
                    ? (closedPositions.Count(p => p.RealizedPnL > 0) * 100m) / closedPositions.Count 
                    : 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting paper trading summary");
            return new();
        }
    }
}

/// <summary>
/// Paper trading summary DTO
/// </summary>
public class PaperTradingSummary
{
    public int OpenPositionsCount { get; set; }
    public int ClosedPositionsCount { get; set; }
    public decimal TotalUnrealizedPnL { get; set; }
    public decimal TotalRealizedPnL { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal WinRate { get; set; }
}
