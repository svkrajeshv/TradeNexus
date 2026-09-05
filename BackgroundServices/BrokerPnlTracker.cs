using NexusApp.Brokers.AngelOne;
using NexusApp.Helpers;
using NexusApp.Interfaces;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Live broker P&amp;L source that does NOT poll the rate-limited position book on
/// every UI tick.
///
/// Angel One SmartAPI exposes no P&amp;L WebSocket - SmartStream only streams market
/// data (LTP/quote/depth) and order status. Live terminal P&amp;L is therefore derived:
///   * a REST snapshot (2s during market hours, 5min after close) supplies the
///     open legs, average prices and broker-booked realised P&amp;L, and
///   * SmartStream LTP ticks re-mark the open legs continuously in between, so
///     unrealised P&amp;L moves in real time without any further REST calls.
/// </summary>
public sealed class BrokerPnlTracker(
    ILogger<BrokerPnlTracker> logger,
    IServiceProvider serviceProvider,
    AngelOneWebSocketClient ws) : BackgroundService
{
    /// <summary>
    /// REST snapshot cadence while the exchange is open. Fast enough that the position
    /// book (new/closed legs, broker-booked realised P&amp;L) tracks the terminal closely;
    /// SmartStream LTP ticks still re-mark the open legs continuously in between.
    /// Set to Angel One's published 1 req/s getPosition limit.
    /// </summary>
    private static readonly TimeSpan MarketHoursInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Cadence once the session is over. The book cannot change with the exchange
    /// closed, so this only needs to be frequent enough to notice the next session
    /// starting - it is not a data-freshness concern.
    /// </summary>
    private static readonly TimeSpan OffHoursInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<BrokerPnlTracker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly AngelOneWebSocketClient _ws = ws;

    private readonly Lock _gate = new();
    private List<BrokerPosition> _snapshot = [];
    private DateTime? _snapshotAtUtc;

    // Guards the single post-close snapshot so the loop goes quiet afterwards.
    private bool _idleSnapshotTaken;

    /// <summary>
    /// Latest live figures, or <c>null</c> when no broker snapshot has succeeded yet.
    /// Realised comes straight from the broker; unrealised is re-marked from live ticks
    /// when available and falls back to the snapshot value otherwise.
    /// </summary>
    public (decimal Unrealized, decimal Realized, int OpenCount)? TryGetLivePnl()
    {
        List<BrokerPosition> snapshot;
        lock (_gate)
        {
            if (_snapshotAtUtc is null)
                return null;
            snapshot = _snapshot;
        }

        var realized = 0m;
        var unrealized = 0m;
        var openCount = 0;

        foreach (var position in snapshot)
        {
            realized += position.RealizedPnL;

            if (position.Quantity == 0m)
                continue;

            openCount++;
            unrealized += MarkToMarket(position);
        }

        return (unrealized, realized, openCount);
    }

    private decimal MarkToMarket(BrokerPosition position)
    {
        if (position.AveragePrice <= 0m || string.IsNullOrWhiteSpace(position.SymbolToken))
            return position.UnrealizedPnL;

        var ltp = _ws.GetLastLtp(position.SymbolToken);
        return ltp > 0m
            ? (ltp - position.AveragePrice) * position.Quantity
            : position.UnrealizedPnL;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Broker P&L tracker started (snapshot every {MarketSeconds}s during market hours, {OffMinutes}min after close; marks from SmartStream ticks)",
            MarketHoursInterval.TotalSeconds, OffHoursInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshSnapshotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Broker P&L snapshot refresh failed; keeping previous snapshot");
            }

            try
            {
                var delay = MarketHours.IsPollingWindowNow() ? MarketHoursInterval : OffHoursInterval;
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken ct)
    {
        // The position book cannot change while the exchange is closed, so polling it
        // outside the session only spends Angel One rate-limit budget (1 req/s on
        // getPosition) that the live session needs. One final snapshot is still taken
        // after the window closes so the end-of-day figures reflect the last fills.
        if (!MarketHours.IsPollingWindowNow())
        {
            if (_idleSnapshotTaken)
                return;

            _idleSnapshotTaken = true;
            _logger.LogInformation(
                "Market closed - taking a final broker P&L snapshot, then pausing getPosition polling until the next session");
        }
        else
        {
            _idleSnapshotTaken = false;
        }

        using var scope = _serviceProvider.CreateScope();
        var broker = scope.ServiceProvider.GetRequiredService<IBroker>();

        if (!broker.IsConnected)
            return;

        var book = await broker.GetPositionBookAsync();

        lock (_gate)
        {
            _snapshot = book;
            _snapshotAtUtc = DateTime.UtcNow;
        }

        var tokens = book
            .Where(p => p.Quantity != 0m
                        && !string.IsNullOrWhiteSpace(p.SymbolToken)
                        && !string.IsNullOrWhiteSpace(p.Exchange))
            .Select(p => (Token: p.SymbolToken!, Exchange: p.Exchange!))
            .Distinct()
            .ToList();

        if (tokens.Count > 0 && _ws.IsOpen)
        {
            await _ws.SubscribeAsync(tokens, ct);
        }

        _logger.LogDebug(
            "Broker P&L snapshot refreshed: {Rows} rows, {Tokens} open tokens subscribed for live marks",
            book.Count, tokens.Count);
    }
}
