using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Helpers;
using NexusApp.Hubs;
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

    // Once a daily risk limit is hit, every open position in scope keeps being squared off
    // for the rest of the day (failed exits and positions opened afterwards). Attempts per
    // position are throttled so an in-flight broker exit is not duplicated.
    private static readonly TimeSpan RiskSquareOffRetryInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<int, DateTime> _riskSquareOffAttemptUtc = [];

    // Track risk limit notifications sent today (IST) to avoid spamming Telegram on every 2-second tick.
    // Keys: "global_profit", "global_loss", $"account_{accId}_profit", $"account_{accId}_loss"
    private readonly HashSet<string> _sentRiskNotificationsToday = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRiskNotificationDateIst = DateTime.MinValue;

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

        // 1. Auto-close any expired option positions lingering in the database (both live and paper)
        // so they are marked closed, realized P&L is settled, and quote polling stops immediately.
        var allOpenPositions = await db.Positions
            .Include(p => p.Signal)
            .Include(p => p.TradingAccount)
            .Where(p => p.ClosedAt == null)
            .ToListAsync(ct);

        bool anyExpiredClosed = false;
        foreach (var pos in allOpenPositions)
        {
            if (ContractExpiryHelper.IsExpiredOptionSymbol(pos.Symbol))
            {
                var exitPrice = pos.CurrentPrice > 0 ? pos.CurrentPrice : 0m;
                _logger.LogInformation(
                    "Auto-closing expired option position {Symbol} (id {Id}, Account {Account}) at price {ExitPrice}",
                    pos.Symbol, pos.Id, pos.TradingAccount?.Name ?? "Unknown", exitPrice);

                pos.ClosedAt = DateTime.UtcNow;
                pos.ClosingPrice = exitPrice;
                pos.RealizedPnL = PnlCalculator.RealizedPnl(pos.EntryPrice, exitPrice, pos.Quantity);
                pos.UnrealizedPnL = 0m;
                pos.UnrealizedPnLPercentage = 0m;
                anyExpiredClosed = true;
            }
        }

        if (anyExpiredClosed)
        {
            await db.SaveChangesAsync(ct);
            var hub = scope.ServiceProvider.GetService<IHubContext<TradingHub>>();
            if (hub is not null)
            {
                await hub.Clients.All.SendAsync("PositionChanged", new { Timestamp = DateTime.UtcNow }, ct);
            }
        }

        // 1. Mark open live positions to the latest LTP so the dashboard's
        // live MTM is accurate and evaluate locally managed SL/target exits.
        var livePositions = allOpenPositions
            .Where(p => p.ClosedAt == null && p.TradingAccount != null && p.TradingAccount.ClientId != "PAPER")
            .ToList();

        var engine = scope.ServiceProvider.GetRequiredService<ITradingEngine>();

        if (livePositions.Count > 0)
        {
            var liveAccountIds = livePositions.Select(p => p.TradingAccountId).Distinct().ToList();
            var liveAccounts = await db.TradingAccounts.AsNoTracking()
                .Where(a => liveAccountIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, ct);

            var brokerCache = new Dictionary<int, IBroker>();

            foreach (var position in livePositions)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (!liveAccounts.TryGetValue(position.TradingAccountId, out var account))
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
                    // for SL/target and risk-limit evaluation. Skips a save if the price is unchanged
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

                    // Self-healing: if an open position has null SL or empty Targets, backfill from linked Signal
                    if ((position.StopLoss == null || position.Targets.Count == 0) && position.Signal != null)
                    {
                        var healed = false;
                        if (position.StopLoss == null && position.Signal.StopLoss > 0)
                        {
                            position.StopLoss = position.Signal.StopLoss;
                            healed = true;
                        }
                        if (position.Targets.Count == 0 && position.Signal.Targets.Count > 0)
                        {
                            position.Targets = position.Signal.Targets.ToList();
                            healed = true;
                        }
                        if (healed)
                        {
                            position.ManagedLocally = true;
                            await db.SaveChangesAsync(ct);
                            _logger.LogInformation("Self-healed missing SL/Targets for live position {Symbol} from Signal: SL={SL}, Targets={Targets}",
                                position.Symbol, position.StopLoss, string.Join("/", position.Targets));
                        }
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

        // 2. Risk Limits Evaluation:
        // Evaluates Daily Max Profit and Daily Max Loss for each enabled account
        // (both LIVE and PAPER accounts) as well as Global Portfolio limits,
        // and sends Telegram notifications whenever Day Target or Day SL is reached.
        var remainingOpenPositions = await db.Positions
            .Include(p => p.TradingAccount)
            .Where(p => p.ClosedAt == null)
            .ToListAsync(ct);

        var allEnabledAccounts = await db.TradingAccounts.AsNoTracking()
            .Where(a => a.IsEnabled)
            .ToDictionaryAsync(a => a.Id, ct);

        await CheckRiskLimitsAsync(scope.ServiceProvider, db, remainingOpenPositions, allEnabledAccounts, ct);
    }

    /// <summary>
    /// Checks both tiers of risk limits and sends Telegram notifications:
    /// 1. Global Portfolio Limits: combined P&L across all accounts (live + paper).
    ///    If hit, sends Telegram notification and auto squares off positions if enabled.
    /// 2. Per-Account Limits: evaluated for all enabled accounts individually.
    ///    If hit, sends Telegram notification and auto squares off positions if enabled.
    /// Both tracked per IST trading day to prevent duplicate notifications.
    /// </summary>
    /// <summary>
    /// Returns the positions whose last risk square-off attempt is older than
    /// <see cref="RiskSquareOffRetryInterval"/> and stamps them as attempted now.
    /// </summary>
    private List<int> DuePositions(IEnumerable<int> positionIds)
    {
        var now = DateTime.UtcNow;
        var due = new List<int>();
        foreach (var id in positionIds)
        {
            if (_riskSquareOffAttemptUtc.TryGetValue(id, out var last) && now - last < RiskSquareOffRetryInterval)
                continue;
            _riskSquareOffAttemptUtc[id] = now;
            due.Add(id);
        }
        return due;
    }

    private async Task CheckRiskLimitsAsync(
        IServiceProvider scopedServices,
        TradingDbContext db,
        List<Position> openPositions,
        Dictionary<int, Models.TradingAccount> accounts,
        CancellationToken ct)
    {
        var settings = scopedServices.GetRequiredService<ISettingsService>();
        var todayIst = DateTime.UtcNow.ToIst().Date;

        if (todayIst != _lastRiskNotificationDateIst)
        {
            _sentRiskNotificationsToday.Clear();
            _lastRiskNotificationDateIst = todayIst;
        }

        var riskManager = scopedServices.GetRequiredService<TradingEngine.RiskManager>();
        var engine = scopedServices.GetRequiredService<ITradingEngine>();
        var notifications = scopedServices.GetService<INotificationService>();
        var autoSquareOffEnabled = await settings.GetSettingAsync<bool?>("RiskAutoSquareOffEnabled") ?? true;

        // -------------------------------------------------------------
        // TIER 2: GLOBAL PORTFOLIO CHECK (LIVE + PAPER COMBINED)
        // -------------------------------------------------------------
        try
        {
            var globalMetrics = await riskManager.GetGlobalPortfolioRiskMetricsAsync();

            bool globalProfitHit = globalMetrics.DailyMaxProfit > 0 && globalMetrics.DailyPnL >= globalMetrics.DailyMaxProfit;
            bool globalLossHit = globalMetrics.DailyMaxLoss > 0 && globalMetrics.DailyPnL <= -globalMetrics.DailyMaxLoss;

            if (todayIst != _lastRiskNotificationDateIst)
            {
                _riskSquareOffAttemptUtc.Clear();
            }

            var globalLatched = _lastGlobalRiskTriggeredDate == todayIst;
            if (globalProfitHit || globalLossHit || globalLatched)
            {
                var isProfit = globalProfitHit || (!globalLossHit && globalMetrics.DailyPnL >= 0);
                var notifKey = isProfit ? "global_profit" : "global_loss";
                var limit = isProfit ? globalMetrics.DailyMaxProfit : globalMetrics.DailyMaxLoss;
                var reason = isProfit
                    ? $"Global Portfolio Daily Max Profit Target Reached (Combined P&L: ₹{globalMetrics.DailyPnL:N2}, Target: ₹{limit:N2})"
                    : $"Global Portfolio Daily Max Loss Limit Reached (Combined P&L: ₹{globalMetrics.DailyPnL:N2}, Limit: ₹{limit:N2})";

                // Fetch ALL open positions across all accounts (live AND paper)
                var allOpenPositionIds = await db.Positions
                    .AsNoTracking()
                    .Where(p => p.ClosedAt == null)
                    .Select(p => p.Id)
                    .ToListAsync(ct);

                int closed = 0;
                int failed = 0;

                if (autoSquareOffEnabled && allOpenPositionIds.Count > 0)
                {
                    var due = DuePositions(allOpenPositionIds);
                    if (due.Count > 0)
                        _logger.LogWarning(
                            "PORTFOLIO RISK HIT: {Reason} — auto squaring off {Count} open position(s) across ALL accounts",
                            reason, due.Count);

                    _lastGlobalRiskTriggeredDate = todayIst;

                    foreach (var posId in due)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (await engine.SquareOffPositionAsync(posId, reason))
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
                }

                // Send Telegram Notification once per day
                if (notifications != null && _sentRiskNotificationsToday.Add(notifKey))
                {
                    var icon = isProfit ? "🎯" : "🛑";
                    var header = isProfit ? "GLOBAL PORTFOLIO DAILY PROFIT TARGET REACHED" : "GLOBAL PORTFOLIO DAILY MAX LOSS LIMIT REACHED";
                    var statusText = isProfit
                        ? "Portfolio daily profit target has been achieved."
                        : "Portfolio daily maximum loss threshold has been breached.";

                    var openText = allOpenPositionIds.Count > 0
                        ? $"• Square-off: {closed} of {allOpenPositionIds.Count} position(s) squared off" + (failed > 0 ? $", {failed} failed" : string.Empty)
                        : "• Open Positions: None (all positions closed)";

                    var msg = $"{icon} {header}!\n\n"
                        + $"• Combined P&L: ₹{globalMetrics.DailyPnL:N2}\n"
                        + $"• Limit: ₹{limit:N2}\n"
                        + $"• Status: {statusText}\n"
                        + openText + "\n"
                        + $"• New Orders: Blocked for remainder of day\n"
                        + $"• Time: {DateTime.UtcNow.ToIstString("hh:mm:ss tt")} IST";

                    try { await notifications.SendTelegramCustomNotificationAsync(msg); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Global risk Telegram notification failed"); }

                    var hub = scopedServices.GetService<IHubContext<TradingHub>>();
                    if (hub != null)
                    {
                        await hub.Clients.All.SendAsync("RiskLimitAlert", new { Timestamp = DateTime.UtcNow }, ct);
                        await hub.Clients.All.SendAsync("PositionChanged", new { Timestamp = DateTime.UtcNow }, ct);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed evaluating global portfolio risk limits");
        }

        // -------------------------------------------------------------
        // TIER 1: PER-ACCOUNT CHECK (INDIVIDUAL ACCOUNT LIMITS)
        // Evaluates ALL enabled accounts (Live & Paper)
        // -------------------------------------------------------------
        foreach (var (accountId, account) in accounts)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var metrics = await riskManager.GetRiskMetricsAsync(accountId);

                bool accProfitHit = metrics.DailyMaxProfit > 0 && metrics.DailyPnL >= metrics.DailyMaxProfit;
                bool accLossHit = metrics.DailyMaxLoss > 0 && metrics.DailyPnL <= -metrics.DailyMaxLoss;

                var accLatched = _lastRiskTriggeredDateByAccount.GetValueOrDefault(accountId, DateTime.MinValue) == todayIst;
                if (!accProfitHit && !accLossHit && !accLatched)
                    continue;

                var isProfit = accProfitHit || (!accLossHit && metrics.DailyPnL >= 0);
                var notifKey = isProfit ? $"account_{accountId}_profit" : $"account_{accountId}_loss";
                var limit = isProfit ? metrics.DailyMaxProfit : metrics.DailyMaxLoss;
                var reason = isProfit
                    ? $"Account Daily Max Profit Target Reached (P&L: ₹{metrics.DailyPnL:N2}, Target: ₹{limit:N2})"
                    : $"Account Daily Max Loss Limit Reached (P&L: ₹{metrics.DailyPnL:N2}, Limit: ₹{limit:N2})";

                var openPositionIds = await db.Positions
                    .AsNoTracking()
                    .Where(p => p.TradingAccountId == accountId && p.ClosedAt == null)
                    .Select(p => p.Id)
                    .ToListAsync(ct);

                int closed = 0;
                int failed = 0;

                if (autoSquareOffEnabled && openPositionIds.Count > 0)
                {
                    var due = DuePositions(openPositionIds);
                    if (due.Count > 0)
                        _logger.LogWarning(
                            "Account {Name}: {Reason} — auto squaring off {Count} open position(s)",
                            account.Name, reason, due.Count);

                    _lastRiskTriggeredDateByAccount[accountId] = todayIst;

                    foreach (var positionId in due)
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
                }

                // Send Telegram Notification once per day
                if (notifications != null && _sentRiskNotificationsToday.Add(notifKey))
                {
                    var icon = isProfit ? "🎯" : "🛑";
                    var header = isProfit ? $"DAILY PROFIT TARGET REACHED — {account.Name}" : $"DAILY MAX LOSS LIMIT REACHED — {account.Name}";
                    var statusText = isProfit
                        ? $"Daily profit target achieved for account '{account.Name}'."
                        : $"Daily maximum loss limit hit for account '{account.Name}'.";

                    var openText = openPositionIds.Count > 0
                        ? $"• Square-off: {closed} of {openPositionIds.Count} position(s) squared off" + (failed > 0 ? $", {failed} failed" : string.Empty)
                        : "• Open Positions: None (all positions closed)";

                    var msg = $"{icon} {header}!\n\n"
                        + $"• Account: {account.Name} ({account.ClientId})\n"
                        + $"• Daily P&L: ₹{metrics.DailyPnL:N2}\n"
                        + $"• Limit: ₹{limit:N2}\n"
                        + $"• Status: {statusText}\n"
                        + openText + "\n"
                        + $"• New Orders: Blocked for this account today\n"
                        + $"• Time: {DateTime.UtcNow.ToIstString("hh:mm:ss tt")} IST";

                    try { await notifications.SendTelegramCustomNotificationAsync(msg); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Account risk Telegram notification failed for {Name}", account.Name); }

                    var hub = scopedServices.GetService<IHubContext<TradingHub>>();
                    if (hub != null)
                    {
                        await hub.Clients.All.SendAsync("RiskLimitAlert", new { Timestamp = DateTime.UtcNow }, ct);
                        await hub.Clients.All.SendAsync("PositionChanged", new { Timestamp = DateTime.UtcNow }, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed evaluating risk limits for account {Name}", account.Name);
            }
        }
    }
}
