using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NexusApp.Brokers.AngelOne;
using NexusApp.Data;
using NexusApp.Helpers;
using NexusApp.Hubs;
using NexusApp.Interfaces;
using NexusApp.Models;
using NexusApp.TradingEngine;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Periodically refreshes position marks, checks broker connectivity and pushes
/// health/status updates to connected UI clients.
/// </summary>
public sealed class HealthMonitorService(
    ILogger<HealthMonitorService> logger,
    IServiceProvider serviceProvider,
    IHubContext<TradingHub> hub,
    NexusApp.Services.HealthSnapshotCache healthSnapshots,
    NexusApp.Services.McxToggle mcx) : BackgroundService
{
    // Reduced from 30s -> 10s. Safe to do because ReadBrokerPnlAsync now prefers the
    // BrokerPnlTracker cache (fed continuously by SmartStream ticks) over a direct
    // broker getPosition call - the comment on that call site notes the previous
    // 30s-cadence direct call is what previously tripped Angel One's rate limit.
    // The health tick itself is otherwise a local DB read + in-memory broadcast, so
    // a tighter cadence only costs a bit more DB/CPU work, not extra broker calls.
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(10);
    private DateTime? _lastSquareOffDateLocal;

    private readonly ILogger<HealthMonitorService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IHubContext<TradingHub> _hub = hub;
    private readonly NexusApp.Services.HealthSnapshotCache _healthSnapshots = healthSnapshots;
    private readonly NexusApp.Services.McxToggle _mcx = mcx;

    /// <summary>
    /// True while any enabled segment is in its polling window. With commodity trading
    /// off this collapses to the equity window, leaving cadence unchanged from before
    /// MCX support existed.
    /// </summary>
    private async Task<bool> IsPollingWindowAsync(CancellationToken ct = default) =>
        MarketHours.IsAnySegmentPollingWindowNow(await _mcx.IsEnabledAsync(ct));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Health monitor started (cadence {Seconds}s)", Cadence.TotalSeconds);

        // Initialize _lastSquareOffDateLocal on startup if we are already past the auto square-off time today
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var autoSquareOffEnabled = await settings.GetSettingAsync<bool?>("AutoSquareOffEnabled") ?? false;
            var autoSquareOffTime = await settings.GetSettingAsync<string>("AutoSquareOffTime") ?? "15:10";
            if (autoSquareOffEnabled && TimeSpan.TryParse(autoSquareOffTime, CultureInfo.InvariantCulture, out TimeSpan targetTime))
            {
                var nowIst = DateTime.UtcNow.ToIst();
                if (nowIst.TimeOfDay >= targetTime)
                {
                    _lastSquareOffDateLocal = nowIst.Date;
                    _logger.LogInformation("Startup: Current time is past configured Auto Square-Off time ({Configured}). Setting last run date to today.", autoSquareOffTime);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to initialize auto square-off date tracker");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var broker = scope.ServiceProvider.GetRequiredService<IBroker>();
                var engine = scope.ServiceProvider.GetRequiredService<ITradingEngine>();

                // Position reconciliation hits the rate-limited getPosition endpoint.
                // The book is static once the exchange closes, so this is confined to
                // the trading window; BrokerPnlTracker keeps the last snapshot for the
                // end-of-day figures.
                if (broker.IsConnected && await IsPollingWindowAsync(stoppingToken))
                {
                    await engine.UpdatePositionsAsync();
                }

                var pnl = await LogAndComputePnlAsync(scope.ServiceProvider, broker, stoppingToken);

                // Connectivity is always reported. The P&L fields are only added when we
                // actually computed them - the client applies fields it finds and keeps
                // its previous values otherwise, so a failed tick leaves the cards on the
                // last good figures instead of flashing 0.00.
                var tick = new Dictionary<string, object?>
                {
                    ["BrokerConnected"] = broker.IsConnected,
                    ["Broker"] = broker.BrokerName,
                    ["Timestamp"] = DateTime.UtcNow
                };

                if (pnl is { } p)
                {
                    tick["BrokerPnlAvailable"] = p.BrokerAvailable;

                    if (p.BrokerAvailable)
                    {
                        var liveUnrealized = p.BrokerUnrealized;
                        var liveRealized = p.BrokerRealized;

                        _logger.LogInformation(
                            "HealthTick live figures sourced from BROKER position book: unrealised {Unrealised:N2} | realised {Realised:N2}",
                            liveUnrealized, liveRealized);

                        tick["LiveUnrealized"] = liveUnrealized;
                        tick["LiveRealized"] = liveRealized;
                        tick["LiveNetPnL"] = liveUnrealized + liveRealized;

                        // Include broker open positions so the Dashboard grid can
                        // render terminal positions that have no local DB row.
                        var tracker = scope.ServiceProvider.GetService<BrokerPnlTracker>();
                        if (tracker is not null)
                        {
                            tick["BrokerPositions"] = tracker.GetOpenPositions()
                                .Select(bp => new
                                {
                                    bp.Symbol,
                                    bp.Quantity,
                                    AvgPrice = bp.AveragePrice,
                                    LTP = bp.CurrentPrice,
                                    PnL = bp.UnrealizedPnL
                                })
                                .ToList();
                        }
                    }
                    else
                    {
                        _logger.LogWarning("HealthTick broker P&L unavailable; live cards will be marked unavailable until next successful broker read.");
                    }

                    tick["LiveOpenPositions"] = p.LiveOpenCount;
                    tick["PaperUnrealized"] = p.PaperUnrealized;
                    tick["PaperRealized"] = p.PaperRealized;
                    tick["PaperNetPnL"] = p.PaperUnrealized + p.PaperRealized;
                    tick["PaperOpenPositions"] = p.PaperOpenCount;
                }

                // Cache before broadcasting so a page that renders between two ticks
                // can pick the figures up immediately instead of waiting for the next one.
                var cachedTracker = scope.ServiceProvider.GetService<BrokerPnlTracker>();
                _healthSnapshots.Latest = new NexusApp.Services.HealthSnapshot(
                    broker.IsConnected,
                    pnl is { } snap && snap.BrokerAvailable,
                    pnl?.BrokerUnrealized ?? 0m,
                    pnl?.BrokerRealized ?? 0m,
                    DateTime.UtcNow,
                    cachedTracker?.GetOpenPositions());

                await _hub.Clients.All.SendAsync("HealthTick", tick, stoppingToken);

                // Auto Square-Off Check
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var autoSquareOffEnabled = await settings.GetSettingAsync<bool?>("AutoSquareOffEnabled") ?? false;
                var autoSquareOffTime = await settings.GetSettingAsync<string>("AutoSquareOffTime") ?? "15:10";

                if (autoSquareOffEnabled && TimeSpan.TryParse(autoSquareOffTime, CultureInfo.InvariantCulture, out TimeSpan targetTime))
                {
                    var nowIst = DateTime.UtcNow.ToIst();
                    var todayLocal = nowIst.Date;
                    if (nowIst.TimeOfDay >= targetTime && (_lastSquareOffDateLocal == null || _lastSquareOffDateLocal < todayLocal))
                    {
                        _logger.LogInformation("Auto Square-Off triggered at {Time} IST (configured time: {Configured})", nowIst.ToString("hh:mm:ss tt"), autoSquareOffTime);
                        _lastSquareOffDateLocal = todayLocal;

                        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                        var openPositions = await db.Positions.Where(p => p.ClosedAt == null).ToListAsync(stoppingToken);
                        if (openPositions.Count > 0)
                        {
                            _logger.LogInformation("Auto squaring off {Count} open position(s) at Market price.", openPositions.Count);
                            foreach (var positionId in openPositions.Select(p => p.Id))
                            {
                                try
                                {
                                    await engine.SquareOffPositionAsync(positionId);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to auto square off position {PositionId}", positionId);
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health monitor tick failed, continuing");
            }

            try { await Task.Delay(Cadence, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Health monitor stopped");
    }

    /// <summary>
    /// Aggregate P&amp;L for a single health tick, split by simulated (paper) vs real
    /// broker accounts.
    /// <para>
    /// <paramref name="BrokerAvailable"/> distinguishes "the broker book was read and
    /// reports a flat 0.00" from "the broker book could not be read". Both produce a
    /// zero figure, so testing the value alone silently substitutes local numbers for a
    /// genuinely flat book.
    /// </para>
    /// </summary>
    private readonly record struct PnlSnapshot(
        decimal PaperUnrealized,
        decimal PaperRealized,
        int PaperOpenCount,
        decimal LiveUnrealized,
        decimal LiveRealized,
        int LiveOpenCount,
        bool BrokerAvailable,
        decimal BrokerUnrealized,
        decimal BrokerRealized);

    /// <summary>
    /// Reads the freshly-marked positions, logs a per-symbol MTM breakdown plus a
    /// session summary to the console/Output window, and returns the aggregate
    /// numbers so they can be pushed to the UI on the same health tick.
    /// "Realized today" is scoped to the current IST trading day.
    /// Returns <c>null</c> when the figures could not be computed, so the caller can
    /// omit them from the broadcast rather than publishing misleading zeros.
    /// </summary>
    private async Task<PnlSnapshot?> LogAndComputePnlAsync(
        IServiceProvider scopedProvider,
        IBroker broker,
        CancellationToken cancellationToken)
    {
        try
        {
            var db = scopedProvider.GetRequiredService<TradingDbContext>();

            var open = await db.Positions
                .AsNoTracking()
                .Where(p => p.ClosedAt == null)
                .Select(p => new
                {
                    p.Symbol,
                    p.Quantity,
                    p.EntryPrice,
                    p.CurrentPrice,
                    p.UnrealizedPnL,
                    IsPaper = EF.Functions.Like(p.TradingAccount.ClientId, BookScope.PaperClientId)
                })
                .ToListAsync(cancellationToken);

            // Positions store UTC timestamps, so translate "today in IST" into a UTC
            // window before querying, keeping the filter translatable by EF Core.
            // The window is half-open so it matches the dashboard's realised query
            // exactly; an open-ended ">=" would also pick up future-dated closures.
            var istToday = DateTime.UtcNow.ToIst().Date;
            var istDayStartUtc = istToday.IstToUtc();
            var istNextDayStartUtc = istToday.AddDays(1).IstToUtc();
            var realized = await db.Positions
                .AsNoTracking()
                .Where(p => p.ClosedAt >= istDayStartUtc && p.ClosedAt < istNextDayStartUtc)
                .GroupBy(p => EF.Functions.Like(p.TradingAccount.ClientId, BookScope.PaperClientId))
                .Select(g => new { IsPaper = g.Key, Total = g.Sum(p => p.RealizedPnL ?? 0m) })
                .ToListAsync(cancellationToken);

            var paperUnrealized = open.Where(p => p.IsPaper).Sum(p => p.UnrealizedPnL);
            var liveUnrealized = open.Where(p => !p.IsPaper).Sum(p => p.UnrealizedPnL);
            var paperRealized = realized.Where(r => r.IsPaper).Sum(r => r.Total);
            var liveRealized = realized.Where(r => !r.IsPaper).Sum(r => r.Total);

            // Broker truth: the position book carries realised P&L even for legs that
            // are fully squared off (netqty 0), which have no matching open Position row.
            var (Available, Unrealized, Realized) = await ReadBrokerPnlAsync(scopedProvider, broker);

            _logger.LogInformation(
                "LIVE  mtm {Mtm:N2} | realized {Realized:N2} | open {Count}",
                liveUnrealized, liveRealized, open.Count(p => !p.IsPaper));
            _logger.LogInformation(
                "PAPER mtm {Mtm:N2} | realized {Realized:N2} | open {Count}",
                paperUnrealized, paperRealized, open.Count(p => p.IsPaper));

            if (Available)
            {
                _logger.LogInformation(
                    "BROKER mtm {Mtm:N2} | realized {Realized:N2} (from position book)",
                    Unrealized, Realized);
            }

            foreach (var p in open)
            {
                _logger.LogInformation(
                    "  [{Mode}] {Symbol} qty={Qty} entry={Entry:N2} ltp={Ltp:N2} mtm={Pnl:N2}",
                    p.IsPaper ? "PAPER" : "LIVE", p.Symbol, p.Quantity, p.EntryPrice, p.CurrentPrice, p.UnrealizedPnL);
            }

            return new PnlSnapshot(
                paperUnrealized, paperRealized, open.Count(p => p.IsPaper),
                liveUnrealized, liveRealized, open.Count(p => !p.IsPaper),
                Available, Unrealized, Realized);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Do not return a zeroed snapshot: the caller would broadcast it and every
            // live P&L card would read 0.00 with no indication anything went wrong.
            _logger.LogError(ex, "Failed to compute P&L summary for health tick");
            return null;
        }
    }

    /// <summary>
    /// Aggregates the broker position book into unrealised/realised totals.
    /// Deliberately not persisted - it is a live read-through of broker state,
    /// avoiding the problem that squared-off legs have no local Position to map onto.
    /// </summary>
    private async Task<(bool Available, decimal Unrealized, decimal Realized)> ReadBrokerPnlAsync(
        IServiceProvider services, IBroker broker)
    {
        if (!broker.IsConnected)
            return (false, 0m, 0m);

        // Preferred path: figures derived from the slow broker snapshot re-marked by
        // SmartStream ticks. Avoids a getPosition call on every health tick, which
        // is what pushed the account into Angel One's access-rate limit.
        var tracker = services.GetService<BrokerPnlTracker>();
        if (tracker?.TryGetLivePnl() is { } live)
            return (true, live.Unrealized, live.Realized);

        // Fallback only: outside the session the book is static, so a direct
        // getPosition on every tick would burn rate-limit budget for figures
        // that cannot have changed. Report unavailable instead of polling.
        if (!await IsPollingWindowAsync())
            return (false, 0m, 0m);

        try
        {
            var book = await broker.GetPositionBookAsync();
            return (true, book.Sum(p => p.UnrealizedPnL), book.Sum(p => p.RealizedPnL));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read broker position book for P&L");
            return (false, 0m, 0m);
        }
    }
}

/// <summary>
/// Streams CMP updates to SignalR clients using Angel One SmartStream WebSocket 2.0.
/// Resolves each active signal's tradingsymbol to a token via <see cref="AngelInstrumentMaster"/>,
/// subscribes over WS (mode = LTP), and falls back to the broker quote API only when no
/// tick is available (WS not yet connected, or master lookup failed).
/// </summary>
public sealed class CmpStreamingService(
    ILogger<CmpStreamingService> logger,
    IServiceProvider serviceProvider,
    IHubContext<TradingHub> hub,
    AngelOneWebSocketClient ws,
    AngelInstrumentMaster instrumentMaster,
    BrokerPnlTracker brokerPnlTracker) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMilliseconds(250);

    private readonly ILogger<CmpStreamingService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IHubContext<TradingHub> _hub = hub;
    private readonly AngelOneWebSocketClient _ws = ws;
    private readonly AngelInstrumentMaster _instrumentMaster = instrumentMaster;

    // signalKey (tradingsymbol) -> (token, exchange)
    private readonly ConcurrentDictionary<string, (string Token, string Exchange)> _tokenBySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    // Short-lived REST LTP cache per tradingsymbol: (price, timestamp).
    // Used as a fallback while WS hasn't produced a tick yet, throttled so we
    // don't hit the REST API for every signal on every 1s cadence.
    private readonly ConcurrentDictionary<string, (decimal Ltp, DateTime AtUtc)> _restLtpCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RestLtpTtl = TimeSpan.FromSeconds(5);

    // Running session High/Low per tradingsymbol. Tracked server-side so the
    // values persist across UI page refreshes / navigation (the client-side
    // dictionaries in Dashboard.razor are recreated on every render). Keyed by
    // resolved tradingsymbol so all signals on the same contract share H/L.
    private readonly ConcurrentDictionary<string, decimal> _highBySymbol =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> _lowBySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    // IST date the High/Low maps were last valid for. When the trading day rolls
    // over, the running High/Low are cleared so each day starts fresh.
    private DateTime _highLowDateIst = DateTime.MinValue;

    // Signal ids whose CMP has been observed strictly BELOW the entry trigger at
    // least once. A "BUY ABOVE 90" signal is only armed once the contract actually
    // trades below 90; only then may an upward cross through 90 fire the order.
    // This prevents instant execution when the signal arrives while the contract is
    // already trading far above the stated entry. In-memory by design: entries are
    // short-lived (they expire via PendingOrderTimeoutMinutes) and losing the armed
    // state on restart fails safe (the signal simply does not fire).
    private readonly ConcurrentDictionary<int, byte> _entryArmedSignals = new();

    // Throttles BrokerPnlTick broadcasts to 1s intervals instead of the 250ms WS cadence.
    private DateTime _lastBrokerPnlBroadcastUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CMP streaming service started (Angel SmartStream WS, cadence {Seconds}s)", Cadence.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var broker = scope.ServiceProvider.GetRequiredService<IBroker>();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

                // Daily reset: clear running High/Low when the IST trading day rolls
                // over so each day starts fresh.
                var todayIst = DateTime.UtcNow.ToIst().Date;
                if (_highLowDateIst != todayIst)
                {
                    _highBySymbol.Clear();
                    _lowBySymbol.Clear();
                    _highLowDateIst = todayIst;
                }

                // Staleness guard for every signal parked waiting for something to happen
                // (activation message or entry crossing). Runs before the broker/price checks:
                // it must not depend on a live connection or a quotable contract, otherwise the
                // signal stays "Cancelling..." in the UI forever.
                await ExpireStaleAwaitingSignalsAsync(
                    db,
                    scope.ServiceProvider.GetRequiredService<ISettingsService>(),
                    stoppingToken);

                if (!broker.IsConnected)
                {
                    await Task.Delay(Cadence, stoppingToken);
                    continue;
                }

                var liveSignals = await db.TradingSignals
                    .AsNoTracking()
                    .Where(s =>
                        s.Status == SignalStatus.Parsed ||
                        s.Status == SignalStatus.Pending ||
                        s.Status == SignalStatus.Executed ||
                        s.Status == SignalStatus.Failed ||
                        s.Status == SignalStatus.AwaitingEntry ||
                        s.Status == SignalStatus.AwaitingActivation)
                    .OrderByDescending(s => s.ReceivedTimestamp)
                    .Take(30)
                    .ToListAsync(stoppingToken);

                if (liveSignals.Count == 0)
                {
                    await Task.Delay(Cadence, stoppingToken);
                    continue;
                }

                await _instrumentMaster.EnsureLoadedAsync(stoppingToken);

                // Group signals by their resolved trading symbol (via master if needed)
                var symbolMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in liveSignals)
                {
                    var key = ResolveTradingSymbol(s);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (!symbolMap.TryGetValue(key, out var list))
                        symbolMap[key] = list = [];
                    list.Add(s.Id);
                }

                // Resolve tokens for all symbols via instrument master
                var subs = new List<(string Token, string Exchange)>();
                foreach (var sym in symbolMap.Keys)
                {
                    if (_tokenBySymbol.ContainsKey(sym))
                        continue;

                    var entry = _instrumentMaster.FindByTradingSymbol(sym);
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Token))
                    {
                        // A blank exch_seg must not default an MCX contract to NFO: the WS
                        // exchangeType would then be 2 (NSE_FO) and no tick would ever arrive,
                        // leaving the CMP column empty for commodity signals.
                        var exch = string.IsNullOrWhiteSpace(entry.ExchangeSegment)
                            ? MarketSegments.ExchangeForTradingSymbol(sym)
                            : entry.ExchangeSegment;

                        _tokenBySymbol[sym] = (entry.Token, exch);
                        subs.Add((entry.Token, exch));
                        _logger.LogInformation(
                            "CMP: added {Symbol} (token={Token}, {Exch}) to broker watchlist",
                            sym, entry.Token, exch);
                    }
                    else
                    {
                        _logger.LogDebug("CMP: no master entry for {Symbol} — cannot subscribe", sym);
                    }
                }

                if (subs.Count > 0)
                {
                    try { await _ws.SubscribeAsync(subs, stoppingToken); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Angel WS subscribe failed"); }
                }

                var ticks = new List<object>(symbolMap.Count);
                var pricesDict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                var nowUtc = DateTime.UtcNow;

                foreach (var kv in symbolMap)
                {
                    var symbol = kv.Key;
                    if (!_tokenBySymbol.TryGetValue(symbol, out var tk))
                        continue; // no subscription possible

                    decimal quote = _ws.GetLastLtp(tk.Token);

                    if (quote <= 0)
                    {
                        // Serve from short-lived REST cache to avoid hammering Angel every 1s.
                        if (_restLtpCache.TryGetValue(symbol, out var cached) &&
                            nowUtc - cached.AtUtc < RestLtpTtl)
                        {
                            quote = cached.Ltp;
                        }
                        else
                        {
                            try
                            {
                                quote = await broker.GetLiveQuoteAsync(symbol);
                                if (quote > 0)
                                    _restLtpCache[symbol] = (quote, nowUtc);
                            }
                            catch { quote = 0m; }
                        }
                    }

                    if (quote <= 0) continue;

                    pricesDict[symbol] = quote;

                    // Update running session High/Low for this symbol (server-side so
                    // it survives UI refresh/navigation).
                    var high = _highBySymbol.AddOrUpdate(symbol, quote, (_, existing) => quote > existing ? quote : existing);
                    var low = _lowBySymbol.AddOrUpdate(symbol, quote, (_, existing) => quote < existing ? quote : existing);

                    foreach (var signalId in kv.Value)
                    {
                        ticks.Add(new
                        {
                            SignalId = signalId,
                            Cmp = quote,
                            High = high,
                            Low = low,
                            Symbol = symbol,
                            Timestamp = nowUtc
                        });
                    }
                }

                if (pricesDict.Count > 0)
                {
                    var paperEngine = scope.ServiceProvider.GetRequiredService<PaperTradingEngine>();
                    await paperEngine.UpdatePositionPricesAsync(pricesDict);
                }

                if (ticks.Count > 0)
                {
                    await _hub.Clients.All.SendAsync("CmpTick", ticks, stoppingToken);
                }

                // Push live broker P&L and open positions at 1s cadence so the
                // Dashboard stat cards and Open Positions grid update smoothly,
                // without flooding clients on every 250ms WS tick.
                if (nowUtc - _lastBrokerPnlBroadcastUtc >= TimeSpan.FromSeconds(1))
                {
                    _lastBrokerPnlBroadcastUtc = nowUtc;
                    var livePnl = brokerPnlTracker.TryGetLivePnl();
                    if (livePnl is { } pnl)
                    {
                        var brokerOpenPositions = brokerPnlTracker.GetOpenPositions()
                            .Select(p => new
                            {
                                p.Symbol,
                                p.Quantity,
                                AvgPrice = p.AveragePrice,
                                LTP = p.CurrentPrice,
                                PnL = p.UnrealizedPnL
                            })
                            .ToList();

                        await _hub.Clients.All.SendAsync("BrokerPnlTick", new
                        {
                            LiveUnrealized = pnl.Unrealized,
                            LiveRealized = pnl.Realized,
                            OpenCount = pnl.OpenCount,
                            Positions = brokerOpenPositions,
                            Timestamp = nowUtc
                        }, stoppingToken);
                    }
                }

                // --- Entry Price Crossing Trigger ---
                // Check AwaitingEntry signals: execute when CMP crosses entry price,
                // expire when older than 10 minutes (staleness guard).
                if (pricesDict.Count > 0)
                {
                    var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                    var crossingEnabled = await settings.GetSettingAsync<bool?>("EnableEntryPriceCrossingTrigger") ?? false;
                    // Entry offset lowers the effective trigger for Buy signals so the
                    // crossing check uses the same offset-adjusted price the TradingEngine
                    // submits the limit order at (EntryPrice - EntryPriceOffset).
                    var entryOffset = await settings.GetSettingAsync<decimal?>("EntryPriceOffset") ?? 0m;
                    var orderVariety = await settings.GetSettingAsync<string>("OrderVariety") ?? "Robo";
                    var isMarketOrder = string.Equals(orderVariety, "Market", StringComparison.OrdinalIgnoreCase);
                    // When enabled, a signal must first be seen trading BELOW the trigger
                    // before an upward cross can fire it (true breakout confirmation).
                    var requireCrossFromBelow = await settings.GetSettingAsync<bool?>("RequireEntryCrossFromBelow") ?? true;

                    // Use a tracked query so we can update status directly
                    var awaitingSignals = await db.TradingSignals
                        .Where(s => s.Status == SignalStatus.AwaitingEntry)
                        .ToListAsync(stoppingToken);

                    // Drop armed state for signals that are no longer awaiting entry.
                    var awaitingIds = awaitingSignals.Select(s => s.Id).ToHashSet();
                    foreach (var armedId in _entryArmedSignals.Keys.Where(id => !awaitingIds.Contains(id)))
                    {
                        _entryArmedSignals.TryRemove(armedId, out _);
                    }

                    foreach (var sig in awaitingSignals)
                    {
                        if (!crossingEnabled)
                            continue;

                        // Resolve the trading symbol for this signal
                        var symbol = ResolveTradingSymbol(sig);
                        if (!pricesDict.TryGetValue(symbol, out var cmp))
                            continue;

                        // Entry crossing is a breakout confirmation: for both Buy and Sell the
                        // signal is armed only once CMP reaches or passes the stated entry
                        // ("BUY ... ABOVE 260" must not fire below 260). The entry offset is a
                        // limit-price retracement buffer and applies to non-market Buy orders
                        // only; it never applies to market orders, matching TradingEngine.
                        var triggerPrice = sig.Action == SignalAction.Buy && !isMarketOrder && entryOffset > 0
                            ? Math.Max(0.05m, sig.EntryPrice - entryOffset)
                            : sig.EntryPrice;

                        var crossed = PriceComparison.IsGreaterThanOrEqual(cmp, triggerPrice);

                        if (crossed && requireCrossFromBelow && !_entryArmedSignals.ContainsKey(sig.Id))
                        {
                            // CMP is at/above the trigger but the contract was never observed
                            // below it, i.e. the entry level was already gone when the signal
                            // arrived. Chasing it would enter far away from the stated entry,
                            // so hold the signal (it expires via the staleness guard above).
                            _logger.LogInformation(
                                "Entry crossing suppressed for signal {Id}: CMP {Cmp} is already at/above Trigger {Trigger} without ever trading below it (raw entry {Entry}). Waiting for a genuine cross from below.",
                                sig.Id, cmp, triggerPrice, sig.EntryPrice);
                            continue;
                        }

                        if (!crossed && _entryArmedSignals.TryAdd(sig.Id, 0))
                        {
                            // Contract is trading below the trigger — arm the signal so the
                            // next upward cross through the trigger executes it.
                            _logger.LogInformation(
                                "Entry armed for signal {Id}: CMP {Cmp} is below Trigger {Trigger} (raw entry {Entry}).",
                                sig.Id, cmp, triggerPrice, sig.EntryPrice);
                        }

                        if (crossed)
                        {
                            _logger.LogInformation(
                                "Entry crossing triggered for signal {Id}: CMP {Cmp} reached Trigger {Trigger} (action {Action}, order variety {OrderVariety}, raw entry {Entry}, offset {Offset}). Executing...",
                                sig.Id, cmp, triggerPrice, sig.Action, orderVariety, sig.EntryPrice, entryOffset);

                            // Reconstruct ParsedSignal from the DB entity
                            var parsed = new ParsedSignal
                            {
                                Action = sig.Action,
                                Index = sig.Index,
                                Strike = sig.Strike,
                                OptionType = sig.OptionType,
                                EntryPrice = sig.EntryPrice,
                                StopLoss = sig.StopLoss,
                                Targets = [.. sig.Targets],
                                ExpiryDate = sig.ExpiryDate,
                                OriginalMessage = sig.OriginalMessage,
                                SignalTime = sig.TelegramTimestamp,
                                ChannelName = sig.ChannelName,
                                IsValid = true
                            };

                            sig.Status = SignalStatus.Pending;
                            await db.SaveChangesAsync(stoppingToken);

                            _entryArmedSignals.TryRemove(sig.Id, out _);

                            var engine = scope.ServiceProvider.GetRequiredService<ITradingEngine>();
                            var ok = await engine.ExecuteSignalAsync(parsed);

                            // Re-read the signal to pick up status changes made by the engine
                            var fresh = await db.TradingSignals.FindAsync([sig.Id], stoppingToken);
                            if (fresh != null)
                            {
                                fresh.Status = (ok || fresh.Status == SignalStatus.Executed)
                                    ? SignalStatus.Executed
                                    : SignalStatus.Failed;
                                await db.SaveChangesAsync(stoppingToken);

                                await _hub.Clients.All.SendAsync("SignalStatusChanged", new
                                {
                                    fresh.Id,
                                    Status = fresh.Status.ToString()
                                }, stoppingToken);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CMP stream tick failed, continuing");
            }

            try { await Task.Delay(Cadence, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        await _ws.DisposeAsync();
        _logger.LogInformation("CMP streaming service stopped");
    }

    /// <summary>
    /// Best-effort tradingsymbol resolution for a signal:
    ///  1. If <see cref="TradingSignal.Symbol"/> is already a real tradingsymbol, use it.
    ///  2. Else look it up in the instrument master by (underlying, expiry, strike, CE/PE).
    ///     Uses the signal's expiry if present, otherwise the nearest future expiry.
    ///  3. Else fall back to a composite key (won't produce ticks but keeps grouping stable).
    /// </summary>
    /// <summary>
    /// Fails every signal that has been parked in <see cref="SignalStatus.AwaitingEntry"/> or
    /// <see cref="SignalStatus.AwaitingActivation"/> longer than the "Pending Order Auto-Cancel"
    /// window (<c>PendingOrderTimeoutMinutes</c>, 0 = disabled) — the same window the dashboard
    /// counts down before it shows "Cancelling...". AwaitingActivation signals used to be left
    /// untouched, so a call whose channel never posted the activation message stayed live
    /// indefinitely with the countdown stuck at "Cancelling...".
    /// </summary>
    private async Task ExpireStaleAwaitingSignalsAsync(TradingDbContext db, ISettingsService settings, CancellationToken ct)
    {
        var stalenessMinutes = await settings.GetSettingAsync<int?>("PendingOrderTimeoutMinutes") ?? 5;
        if (stalenessMinutes <= 0)
            return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(stalenessMinutes);
        var stale = await db.TradingSignals
            .Where(s => (s.Status == SignalStatus.AwaitingEntry || s.Status == SignalStatus.AwaitingActivation) &&
                        s.TelegramTimestamp < cutoff)
            .ToListAsync(ct);

        if (stale.Count == 0)
            return;

        var defaultAccId = await db.TradingAccounts
            .AsNoTracking()
            .Where(a => a.IsEnabled)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);

        foreach (var sig in stale)
        {
            var previousStatus = sig.Status;
            var signalAge = DateTime.UtcNow - sig.TelegramTimestamp.EnsureUtc();
            var reason = $"Expired: {signalAge.TotalMinutes:0.0}/{stalenessMinutes} min stale";

            sig.Status = SignalStatus.Failed;

            _logger.LogWarning(
                "{Status} signal {Id} expired — {Age:0.0} min old (>{Cutoff} min staleness cutoff)",
                previousStatus, sig.Id, signalAge.TotalMinutes, stalenessMinutes);

            if (defaultAccId > 0)
            {
                db.Orders.Add(new Order
                {
                    SignalId = sig.Id,
                    TradingAccountId = defaultAccId,
                    Symbol = sig.Symbol ?? sig.Index,
                    Quantity = 0,
                    Price = sig.EntryPrice,
                    Side = sig.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell,
                    OrderType = OrderType.Limit,
                    ProductType = ProductType.Nrml,
                    Status = OrderStatus.Rejected,
                    ErrorMessage = reason,
                    CreatedAt = DateTime.UtcNow
                });
            }

            _entryArmedSignals.TryRemove(sig.Id, out _);
        }

        await db.SaveChangesAsync(ct);

        foreach (var sig in stale)
        {
            await _hub.Clients.All.SendAsync("SignalStatusChanged", new
            {
                sig.Id,
                Status = sig.Status.ToString()
            }, ct);
        }
    }

    private string ResolveTradingSymbol(TradingSignal signal)
    {
        var stored = signal.Symbol?.Trim();
        if (!string.IsNullOrWhiteSpace(stored) &&
            _instrumentMaster.FindByTradingSymbol(stored) is not null)
        {
            return stored!.ToUpperInvariant();
        }

        try
        {
            // A commodity signal's expiry is only a month marker ("EXPIRY - SEP"), so it
            // is mapped onto the contract listed in that month rather than used as-is.
            var expiry = DateTimeExtensions.IstToday();
            if (signal.ExpiryDate != default)
            {
                expiry = MarketSegments.IsCommodity(signal.Index)
                    ? _instrumentMaster.ExpiryInMonth(signal.Index, signal.ExpiryDate.Year, signal.ExpiryDate.Month) ?? expiry
                    : signal.ExpiryDate;
            }
            var optType = signal.OptionType.ToString().ToUpperInvariant();
            var entry = _instrumentMaster.FindOption(signal.Index, expiry, signal.Strike, optType);
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.TradingSymbol))
                return entry.TradingSymbol.ToUpperInvariant();
        }
        catch { /* ignore — master lookup failure */ }

        return !string.IsNullOrWhiteSpace(stored)
            ? stored!.ToUpperInvariant()
            : $"{signal.Index}{signal.Strike:0}{signal.OptionType.ToString().ToUpperInvariant()}";
    }
}
