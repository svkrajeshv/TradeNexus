using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Helpers;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Monitors <b>live</b>, locally-managed positions (created from non-Robo Limit
/// order fills where the broker is NOT managing brackets) and squares them off
/// through the real broker when the last-traded price crosses the position's
/// stop-loss or a target.
///
/// Robo/bracket orders (broker-managed) and paper positions are intentionally
/// ignored here — paper exits are simulated by <see cref="TradingEngine.PaperTradingEngine"/>
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

        var positions = await db.Positions
            .Include(p => p.Signal)
            .Where(p => p.ClosedAt == null && p.ManagedLocally)
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

                var ok = await engine.SquareOffPositionAsync(position.Id);
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

}
