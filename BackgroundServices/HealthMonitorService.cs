using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusApp.Brokers.AngelOne;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Periodically refreshes position marks, checks broker connectivity and pushes
/// health/status updates to connected UI clients.
/// </summary>
public sealed class HealthMonitorService : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(30);

    private readonly ILogger<HealthMonitorService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<TradingHub> _hub;

    public HealthMonitorService(
        ILogger<HealthMonitorService> logger,
        IServiceProvider serviceProvider,
        IHubContext<TradingHub> hub)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Health monitor started (cadence {Seconds}s)", Cadence.TotalSeconds);

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

                await _hub.Clients.All.SendAsync("HealthTick", new
                {
                    BrokerConnected = broker.IsConnected,
                    Broker = broker.BrokerName,
                    Timestamp = DateTime.UtcNow
                }, stoppingToken);
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
}

/// <summary>
/// Streams CMP updates to SignalR clients using Angel One SmartStream WebSocket 2.0.
/// Resolves each active signal's tradingsymbol to a token via <see cref="AngelInstrumentMaster"/>,
/// subscribes over WS (mode = LTP), and falls back to the broker quote API only when no
/// tick is available (WS not yet connected, or master lookup failed).
/// </summary>
public sealed class CmpStreamingService : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    private readonly ILogger<CmpStreamingService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<TradingHub> _hub;
    private readonly AngelOneWebSocketClient _ws;
    private readonly AngelInstrumentMaster _instrumentMaster;

    // signalKey (tradingsymbol) -> (token, exchange)
    private readonly ConcurrentDictionary<string, (string Token, string Exchange)> _tokenBySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    // Short-lived REST LTP cache per tradingsymbol: (price, timestamp).
    // Used as a fallback while WS hasn't produced a tick yet, throttled so we
    // don't hit the REST API for every signal on every 1s cadence.
    private readonly ConcurrentDictionary<string, (decimal Ltp, DateTime AtUtc)> _restLtpCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RestLtpTtl = TimeSpan.FromSeconds(5);

    public CmpStreamingService(
        ILogger<CmpStreamingService> logger,
        IServiceProvider serviceProvider,
        IHubContext<TradingHub> hub,
        AngelOneWebSocketClient ws,
        AngelInstrumentMaster instrumentMaster)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _hub = hub;
        _ws = ws;
        _instrumentMaster = instrumentMaster;
    }

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
                        s.Status == SignalStatus.Failed)
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
                        symbolMap[key] = list = new List<int>();
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

                    foreach (var signalId in kv.Value)
                    {
                        ticks.Add(new
                        {
                            SignalId = signalId,
                            Cmp = quote,
                            Symbol = symbol,
                            Timestamp = nowUtc
                        });
                    }
                }

                if (ticks.Count > 0)
                {
                    await _hub.Clients.All.SendAsync("CmpTick", ticks, stoppingToken);
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

    private static string BuildSymbolKey(TradingSignal signal)
    {
        if (!string.IsNullOrWhiteSpace(signal.Symbol))
            return signal.Symbol!.Trim().ToUpperInvariant();
        return $"{signal.Index}{signal.Strike:0}{signal.OptionType.ToString().ToUpperInvariant()}";
    }
}
