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
    IHubContext<TradingHub> hub) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(30);
    private DateTime? _lastSquareOffDateLocal;

    private readonly ILogger<HealthMonitorService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IHubContext<TradingHub> _hub = hub;

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

                if (broker.IsConnected)
                {
                    await engine.UpdatePositionsAsync();
                }

                var (mtm, realizedToday, openCount) = await LogAndComputePnlAsync(scope.ServiceProvider, stoppingToken);

                await _hub.Clients.All.SendAsync("HealthTick", new
                {
                    BrokerConnected = broker.IsConnected,
                    Broker = broker.BrokerName,
                    Mtm = mtm,
                    RealizedToday = realizedToday,
                    NetPnL = mtm + realizedToday,
                    OpenPositions = openCount,
                    Timestamp = DateTime.UtcNow
                }, stoppingToken);

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
                            foreach (var position in openPositions)
                            {
                                try
                                {
                                    await engine.SquareOffPositionAsync(position.Id);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to auto square off position {PositionId}", position.Id);
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
    /// Reads the freshly-marked positions, logs a per-symbol MTM breakdown plus a
    /// session summary to the console/Output window, and returns the aggregate
    /// numbers so they can be pushed to the UI on the same health tick.
    /// "Realized today" is scoped to the current IST trading day.
    /// </summary>
    private async Task<(decimal Mtm, decimal RealizedToday, int OpenCount)> LogAndComputePnlAsync(
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var db = scopedProvider.GetRequiredService<TradingDbContext>();

            var open = await db.Positions
                .AsNoTracking()
                .Where(p => p.ClosedAt == null)
                .Select(p => new { p.Symbol, p.Quantity, p.EntryPrice, p.CurrentPrice, p.UnrealizedPnL })
                .ToListAsync(cancellationToken);

            var mtm = open.Sum(p => p.UnrealizedPnL);

            // Positions store UTC timestamps, so translate "today in IST" into a UTC
            // window before querying, keeping the filter translatable by EF Core.
            var istDayStartUtc = DateTime.UtcNow.ToIst().Date.IstToUtc();
            var realizedToday = await db.Positions
                .AsNoTracking()
                .Where(p => p.ClosedAt != null && p.ClosedAt >= istDayStartUtc)
                .SumAsync(p => p.RealizedPnL ?? 0m, cancellationToken);

            _logger.LogInformation(
                "MTM {Mtm:N2} | Realized {Realized:N2} | Net {Net:N2} | Open {Count}",
                mtm, realizedToday, mtm + realizedToday, open.Count);

            foreach (var p in open)
            {
                _logger.LogInformation(
                    "  {Symbol} qty={Qty} entry={Entry:N2} ltp={Ltp:N2} mtm={Pnl:N2}",
                    p.Symbol, p.Quantity, p.EntryPrice, p.CurrentPrice, p.UnrealizedPnL);
            }

            return (mtm, realizedToday, open.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute P&L summary for health tick");
            return (0m, 0m, 0);
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
    AngelInstrumentMaster instrumentMaster) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

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
                        _tokenBySymbol[sym] = (entry.Token, entry.ExchangeSegment ?? "NFO");
                        subs.Add((entry.Token, entry.ExchangeSegment ?? "NFO"));
                        _logger.LogInformation(
                            "CMP: added {Symbol} (token={Token}, {Exch}) to broker watchlist",
                            sym, entry.Token, entry.ExchangeSegment ?? "NFO");
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
                    // Staleness cutoff follows the same setting used for pending order auto-cancel.
                    var stalenessMinutes = await settings.GetSettingAsync<int?>("PendingOrderTimeoutMinutes") ?? 5;

                    // Use a tracked query so we can update status directly
                    var awaitingSignals = await db.TradingSignals
                        .Where(s => s.Status == SignalStatus.AwaitingEntry)
                        .ToListAsync(stoppingToken);

                    foreach (var sig in awaitingSignals)
                    {
                        // Staleness guard: expire signals older than the configured cutoff (0 = disabled)
                        var signalAge = DateTime.UtcNow - sig.TelegramTimestamp.EnsureUtc();
                        if (stalenessMinutes > 0 && signalAge > TimeSpan.FromMinutes(stalenessMinutes))
                        {
                            sig.Status = SignalStatus.Failed;
                            var reason = $"Expired: {signalAge.TotalMinutes:0.0}/{stalenessMinutes} min stale";
                            _logger.LogWarning(
                                "AwaitingEntry signal {Id} expired — {Age:0.0} min old (>{Cutoff} min staleness cutoff)",
                                sig.Id, signalAge.TotalMinutes, stalenessMinutes);

                            var defaultAccId = await db.TradingAccounts
                                .AsNoTracking()
                                .Where(a => a.IsEnabled)
                                .Select(a => a.Id)
                                .FirstOrDefaultAsync(stoppingToken);

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

                            await db.SaveChangesAsync(stoppingToken);

                            await _hub.Clients.All.SendAsync("SignalStatusChanged", new
                            {
                                sig.Id,
                                Status = sig.Status.ToString()
                            }, stoppingToken);
                            continue;
                        }

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
            var expiry = signal.ExpiryDate != default ? signal.ExpiryDate : DateTime.Today;
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
