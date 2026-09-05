using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Helpers;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Marks all open <b>live</b> positions to the latest traded price so the dashboard
/// reports an accurate live MTM, and squares off <b>locally-managed</b> positions
/// (created from non-Robo Limit order fills where the broker is NOT managing
/// brackets) through the real broker when the price crosses the stop-loss or a target.
///
/// Robo/bracket positions are marked but never exited here, and paper positions are
/// skipped entirely — paper exits are simulated by <see cref="TradingEngine.PaperTradingEngine"/>
/// and Robo exits are handled by the broker.
///
/// SL/target logic assumes a long options BUY (LTP ≤ SL exits at stop, LTP ≥ target exits).
/// </summary>
public sealed class LivePositionMonitorService(
    ILogger<LivePositionMonitorService> logger,
    IServiceProvider services) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(2);

    private readonly ILogger<LivePositionMonitorService> _logger = logger;
    private readonly IServiceProvider _services = services;

    // Tracks the IST trading date on which each account last had its Daily Max
    // Profit/Loss auto square-off triggered, so it fires at most once per
    // account per day (see CheckRiskLimitsAsync below).
    private readonly Dictionary<int, DateTime> _lastRiskTriggeredDateByAccount = [];

    // Tracks the IST trading date when global portfolio risk square-off was triggered.
    private DateTime? _lastGlobalRiskTriggeredDate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Live position monitor started (cadence {Seconds}s)", Cadence.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Live position monitor tick failed, continuing");
            }

            try { await Task.Delay(Cadence, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Live position monitor stopped");
    }

    private async Task MonitorOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        // All open live positions are marked to the latest LTP so the dashboard's
        // live MTM is accurate. Broker-managed (Robo/BO) positions are marked but
        // never exited here - the broker owns their SL/target legs.
        var positions = await db.Positions
            .Include(p => p.Signal)
            .Where(p => p.ClosedAt == null && p.TradingAccount.ClientId != "PAPER")
            .ToListAsync(ct);

        if (positions.Count == 0)
            return;

        var engine = scope.ServiceProvider.GetRequiredService<ITradingEngine>();

        // Resolve each distinct account once per tick (not per position) to avoid an
        // N+1 DB round-trip and repeated keyed-service resolution on every 2s cycle.
        var accountIds = positions.Select(p => p.TradingAccountId).Distinct().ToList();
        var accounts = await db.TradingAccounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var brokerCache = new Dictionary<int, IBroker>();

        await CheckRiskLimitsAsync(scope.ServiceProvider, db, positions, accounts, ct);

        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!accounts.TryGetValue(position.TradingAccountId, out var account))
                    continue;

                if (!brokerCache.TryGetValue(account.Id, out var broker))
                {
                    broker = scope.ServiceProvider.GetRequiredKeyedService<IBroker>(account.BrokerType);
                    brokerCache[account.Id] = broker;
                }

                if (!broker.IsConnected)
                    continue;

                decimal ltp = 0m;
                try { ltp = await broker.GetLiveQuoteAsync(position.Symbol); }
                catch { ltp = 0m; }

                if (ltp <= 0)
                    ltp = position.CurrentPrice;

                if (ltp <= 0)
                    continue;

                // Persist the latest LTP back to DB so the CMP is always fresh
                // for SL/target evaluation. Skips a save if the price is unchanged
                // to avoid unnecessary writes on every 2s tick.
                if (ltp != position.CurrentPrice)
                {
                    position.CurrentPrice = ltp;
                    position.UnrealizedPnL = PnlCalculator.UnrealizedPnl(position.EntryPrice, ltp, position.Quantity);
                    position.UnrealizedPnLPercentage = position.EntryPrice > 0
                        ? (position.UnrealizedPnL / (position.EntryPrice * position.Quantity)) * 100m
                        : 0m;
                    await db.SaveChangesAsync(ct);
                }

                // Broker-managed brackets are only marked, never exited locally.
                if (!position.ManagedLocally)
                    continue;

                var exitReason = ExitEvaluator.Evaluate(position, ltp);
                if (exitReason == ExitReason.None)
                    continue;

                // A broker-side target order may already be resting for this position
                // (plain LIMIT entries). Let the broker own the profit exit; the app only
                // steps in for stop-loss.
                if (exitReason == ExitReason.Target &&
                    await RestingTargetOrders.ExistsAsync(db, position.TradingAccountId, position.Symbol, ct))
                {
                    _logger.LogDebug(
                        "Target reached for {Symbol} but a broker target order is resting; leaving the exit to the broker",
                        position.Symbol);
                    continue;
                }

                _logger.LogInformation(
                    "Live position {Symbol} hit {Reason} (LTP {Ltp}, SL {SL}); squaring off via broker",
                    position.Symbol, exitReason, ltp, position.StopLoss);

                var ok = await engine.SquareOffPositionAsync(
                    position.Id,
                    exitReason == ExitReason.StopLoss ? "Stop Loss Hit" : "Target Hit");
                if (!ok)
                {
                    _logger.LogWarning(
                        "Square-off request for live position {Symbol} (id {Id}) did not succeed; will retry next tick",
                        position.Symbol, position.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed monitoring live position {Symbol} (id {Id})", position.Symbol, position.Id);
            }
        }
    }

    /// <summary>
    /// Checks both tiers of risk limits:
    /// 1. Global Portfolio Limits: combined P&L across all accounts (live + paper).
    ///    If hit, all open positions across all accounts are squared off.
    /// 2. Per-Account Limits: evaluated for each account individually.
    ///    If hit, only that specific account's open positions are squared off.
    /// Both gated by <c>RiskAutoSquareOffEnabled</c> and tracked per IST trading day.
    /// </summary>
    private async Task CheckRiskLimitsAsync(
        IServiceProvider scopedServices,
        TradingDbContext db,
        List<Position> positions,
        Dictionary<int, Models.TradingAccount> accounts,
        CancellationToken ct)
    {
        var settings = scopedServices.GetRequiredService<ISettingsService>();
        var enabled = await settings.GetSettingAsync<bool?>("RiskAutoSquareOffEnabled") ?? false;
        if (!enabled)
            return;

        var todayIst = DateTime.UtcNow.ToIst().Date;
        var riskManager = scopedServices.GetRequiredService<TradingEngine.RiskManager>();
        var engine = scopedServices.GetRequiredService<ITradingEngine>();
        var notifications = scopedServices.GetService<INotificationService>();

        // -------------------------------------------------------------
        // TIER 2: GLOBAL PORTFOLIO CHECK (LIVE + PAPER COMBINED)
        // -------------------------------------------------------------
        if (_lastGlobalRiskTriggeredDate != todayIst)
        {
            try
            {
                var globalMetrics = await riskManager.GetGlobalPortfolioRiskMetricsAsync();

                string? globalReason = null;
                if (globalMetrics.DailyMaxLoss > 0 && globalMetrics.DailyPnL <= -globalMetrics.DailyMaxLoss)
                    globalReason = $"Global Portfolio Daily Max Loss Hit (Combined P&L: \u20B9{globalMetrics.DailyPnL:N2}, Limit: \u20B9{globalMetrics.DailyMaxLoss:N2})";
                else if (globalMetrics.DailyMaxProfit > 0 && globalMetrics.DailyPnL >= globalMetrics.DailyMaxProfit)
                    globalReason = $"Global Portfolio Daily Max Profit Hit (Combined P&L: \u20B9{globalMetrics.DailyPnL:N2}, Limit: \u20B9{globalMetrics.DailyMaxProfit:N2})";

                if (globalReason is not null)
                {
                    // Fetch ALL open positions across all accounts (live AND paper)
                    var allOpenPositionIds = await db.Positions
                        .AsNoTracking()
                        .Where(p => p.ClosedAt == null)
                        .Select(p => p.Id)
                        .ToListAsync(ct);

                    if (allOpenPositionIds.Count > 0)
                    {
                        _logger.LogWarning(
                            "PORTFOLIO RISK HIT: {Reason} \u2014 auto squaring off {Count} open position(s) across ALL accounts",
                            globalReason, allOpenPositionIds.Count);

                        _lastGlobalRiskTriggeredDate = todayIst;

                        var closed = 0;
                        var failed = 0;
                        foreach (var posId in allOpenPositionIds)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                if (await engine.SquareOffPositionAsync(posId, globalReason))
                                    closed++;
                                else
                                    failed++;
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                _logger.LogWarning(ex, "Global risk square-off failed for position {PositionId}", posId);
                            }
                        }

                        if (notifications is not null)
                        {
                            var msg = $"\U0001F6A8 GLOBAL PORTFOLIO AUTO SQUARE-OFF\n\n"
                                + $"\u2022 {globalReason}\n"
                                + $"\u2022 Closed: {closed} of {allOpenPositionIds.Count} position(s) across ALL accounts (Live + Paper)"
                                + (failed > 0 ? $"\n\u2022 Failed: {failed}" : string.Empty)
                                + $"\n\u2022 Time: {DateTime.UtcNow.ToIstString("hh:mm:ss tt")} IST";
                            try { await notifications.SendTelegramCustomNotificationAsync(msg); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Global risk auto square-off Telegram notification failed"); }
                        }

                        // All open positions have been handled; return for this tick
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed evaluating global portfolio risk-limit auto square-off");
            }
        }

        // -------------------------------------------------------------
        // TIER 1: PER-ACCOUNT CHECK (INDIVIDUAL ACCOUNT LIMITS)
        // -------------------------------------------------------------
        var accountIdsWithOpenPositions = positions.Select(p => p.TradingAccountId).Distinct();

        foreach (var accountId in accountIdsWithOpenPositions)
        {
            ct.ThrowIfCancellationRequested();

            if (!accounts.TryGetValue(accountId, out var account) || !account.IsEnabled)
                continue;

            // Reset the per-day guard once a new IST trading day starts.
            if (_lastRiskTriggeredDateByAccount.TryGetValue(accountId, out var triggeredDate) && triggeredDate == todayIst)
                continue;

            try
            {
                var metrics = await riskManager.GetRiskMetricsAsync(accountId);

                string? reason = null;
                if (metrics.DailyMaxLoss > 0 && metrics.DailyPnL <= -metrics.DailyMaxLoss)
                    reason = $"Account Daily Max Loss Hit (P&L: \u20B9{metrics.DailyPnL:N2}, Limit: \u20B9{metrics.DailyMaxLoss:N2})";
                else if (metrics.DailyMaxProfit > 0 && metrics.DailyPnL >= metrics.DailyMaxProfit)
                    reason = $"Account Daily Max Profit Hit (P&L: \u20B9{metrics.DailyPnL:N2}, Limit: \u20B9{metrics.DailyMaxProfit:N2})";

                if (reason is null)
                    continue;

                var openPositionIds = await db.Positions
                    .AsNoTracking()
                    .Where(p => p.TradingAccountId == accountId && p.ClosedAt == null)
                    .Select(p => p.Id)
                    .ToListAsync(ct);

                if (openPositionIds.Count == 0)
                    continue;

                _logger.LogWarning(
                    "Account {Name}: {Reason} \u2014 auto squaring off {Count} open position(s)",
                    account.Name, reason, openPositionIds.Count);

                // Mark as triggered before issuing square-offs so a slow/failing
                // broker call doesn't cause this account to be re-processed every
                // tick for the rest of the day.
                _lastRiskTriggeredDateByAccount[accountId] = todayIst;

                var closed = 0;
                var failed = 0;
                foreach (var positionId in openPositionIds)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (await engine.SquareOffPositionAsync(positionId, reason))
                            closed++;
                        else
                            failed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Risk-limit square-off failed for position {PositionId}", positionId);
                    }
                }

                _logger.LogInformation(
                    "Account {Name} risk-limit square-off complete: closed {Closed}, failed {Failed} of {Total}",
                    account.Name, closed, failed, openPositionIds.Count);

                if (notifications is not null)
                {
                    var msg = $"\U0001F514 ACCOUNT RISK LIMIT AUTO SQUARE-OFF \u2014 {account.Name}\n\n"
                        + $"\u2022 {reason}\n"
                        + $"\u2022 Closed: {closed} of {openPositionIds.Count} position(s)"
                        + (failed > 0 ? $"\n\u2022 Failed: {failed}" : string.Empty)
                        + $"\n\u2022 Time: {DateTime.UtcNow.ToIstString("hh:mm:ss tt")} IST";
                    try { await notifications.SendTelegramCustomNotificationAsync(msg); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Risk-limit auto square-off Telegram notification failed"); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed evaluating risk-limit auto square-off for account {Name}", account.Name);
            }
        }
    }
}
