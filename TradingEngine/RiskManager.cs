using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Models;

namespace NexusApp.TradingEngine;

/// <summary>
/// Risk management engine for validating trading limits
/// </summary>
public class RiskManager
{
    private readonly TradingDbContext _context;
    private readonly ILogger<RiskManager> _logger;

    public RiskManager(TradingDbContext context, ILogger<RiskManager> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Validates if account meets all risk criteria
    /// </summary>
    public async Task<bool> ValidateLimitsAsync(TradingAccount account)
    {
        try
        {
            // Get today's P&L
            var todayOrders = await _context.Orders
                .Where(o => o.TradingAccountId == account.Id
                    && o.CreatedAt.Date == DateTime.Today
                    && o.Status == OrderStatus.Executed)
                .ToListAsync();

            if (!todayOrders.Any())
                return true;

            // Calculate daily P&L (simplified)
            decimal dailyPnL = 0;
            foreach (var order in todayOrders)
            {
                if (order.ExecutedPrice.HasValue)
                {
                    var pnl = (order.ExecutedPrice.Value - order.Price) * order.Quantity;
                    dailyPnL += pnl;
                }
            }

            // Check daily loss limit
            if (dailyPnL < -account.DailyMaxLoss)
            {
                _logger.LogWarning("Daily loss limit exceeded: {Loss}", dailyPnL);
                return false;
            }

            // Check daily profit limit
            if (dailyPnL > account.DailyMaxProfit)
            {
                _logger.LogWarning("Daily profit limit exceeded: {Profit}", dailyPnL);
                return false;
            }

            // Check max trades per day
            if (todayOrders.Count >= account.MaxTradesPerDay)
            {
                _logger.LogWarning("Max trades per day exceeded: {Count}", todayOrders.Count);
                return false;
            }

            // Check max open positions
            var openPositions = await _context.Positions
                .Where(p => p.TradingAccountId == account.Id && p.ClosedAt == null)
                .CountAsync();

            if (openPositions >= account.MaxOpenPositions)
            {
                _logger.LogWarning("Max open positions exceeded: {Count}", openPositions);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating risk limits");
            return false;
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
                return false;
            }

            var allowAfterHours = await _context.ApplicationSettings
                .AsNoTracking()
                .Where(s => s.Key == "AllowAfterMarketHours")
                .Select(s => s.Value)
                .FirstOrDefaultAsync();
            var bypassMarketHours = string.Equals(allowAfterHours, "true", StringComparison.OrdinalIgnoreCase);

            // Check trading hours (9:15 AM – 3:30 PM IST for NSE/BSE) unless bypass is enabled.
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));
            var marketOpen  = new TimeSpan(9, 15, 0);
            var marketClose = new TimeSpan(15, 30, 0);

            if (!bypassMarketHours && (now.TimeOfDay < marketOpen || now.TimeOfDay > marketClose))
            {
                _logger.LogWarning("Trading outside market hours ({Time} IST). Market: 09:15–15:30", now.ToString("HH:mm"));
                return false;
            }

            if (!account.IsEnabled)
            {
                _logger.LogWarning("Account {Id} is disabled", account.Id);
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

            var todayOrders = await _context.Orders
                .Where(o => o.TradingAccountId == accountId && o.CreatedAt.Date == DateTime.Today)
                .CountAsync();

            var openPositions = await _context.Positions
                .Where(p => p.TradingAccountId == accountId && p.ClosedAt == null)
                .CountAsync();

            var dailyPnL = await _context.Orders
                .Where(o => o.TradingAccountId == accountId 
                    && o.CreatedAt.Date == DateTime.Today
                    && o.Status == OrderStatus.Executed
                    && o.ExecutedPrice.HasValue)
                .SumAsync(o => (o.ExecutedPrice.Value - o.Price) * o.Quantity);

            return new RiskMetrics
            {
                TradesPerDayUsed = todayOrders,
                MaxTradesPerDay = account.MaxTradesPerDay,
                OpenPositionsCount = openPositions,
                MaxOpenPositions = account.MaxOpenPositions,
                DailyPnL = dailyPnL,
                DailyMaxLoss = account.DailyMaxLoss,
                DailyMaxProfit = account.DailyMaxProfit
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting risk metrics");
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
/// Paper trading engine for simulated trading
/// </summary>
public class PaperTradingEngine
{
    private readonly TradingDbContext _context;
    private readonly ILogger<PaperTradingEngine> _logger;
    private readonly Dictionary<string, decimal> _lastPrices = new();

    public PaperTradingEngine(TradingDbContext context, ILogger<PaperTradingEngine> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Simulates order execution without broker
    /// </summary>
    public async Task<bool> SimulateOrderAsync(Order order, decimal currentPrice)
    {
        try
        {
            order.Status = OrderStatus.Executed;
            order.ExecutedAt = DateTime.UtcNow;
            order.ExecutedPrice = currentPrice;
            order.FilledQuantity = order.Quantity;

            // Create position
            var position = new Position
            {
                TradingAccountId = order.TradingAccountId,
                SignalId = order.SignalId,
                Symbol = order.Symbol,
                Quantity = order.Quantity,
                EntryPrice = currentPrice,
                CurrentPrice = currentPrice,
                OpenedAt = DateTime.UtcNow,
                UnrealizedPnL = 0
            };

            _context.Positions.Add(position);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Paper order simulated: {Symbol} @ {Price}", order.Symbol, currentPrice);
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
            var positions = await _context.Positions
                .Where(p => p.ClosedAt == null)
                .ToListAsync();

            foreach (var position in positions)
            {
                if (prices.TryGetValue(position.Symbol, out var newPrice))
                {
                    position.CurrentPrice = newPrice;
                    position.UnrealizedPnL = (newPrice - position.EntryPrice) * position.Quantity;
                    position.UnrealizedPnLPercentage = position.EntryPrice > 0 
                        ? (position.UnrealizedPnL / (position.EntryPrice * position.Quantity)) * 100 
                        : 0;
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("Updated {Count} paper positions", positions.Count);
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
            var position = await _context.Positions.FindAsync(positionId);
            if (position == null)
                return false;

            position.ClosedAt = DateTime.UtcNow;
            position.ClosingPrice = closingPrice;
            position.RealizedPnL = (closingPrice - position.EntryPrice) * position.Quantity;

            await _context.SaveChangesAsync();
            _logger.LogInformation("Paper position closed: {Symbol} @ {Price}", position.Symbol, closingPrice);
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
