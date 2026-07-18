using Microsoft.EntityFrameworkCore;
using System.Globalization;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.TradingEngine;

/// <summary>
/// Core trading engine for executing trades. Handles validation, risk checks,
/// symbol resolution, duplicate detection and routing between paper and live modes.
/// </summary>
public class TradingEngine : ITradingEngine
{
    private readonly TradingDbContext _context;
    private readonly IBroker _broker;
    private readonly RiskManager _riskManager;
    private readonly SymbolBuilder _symbolBuilder;
    private readonly PaperTradingEngine _paperEngine;
    private readonly ISettingsService _settings;
    private readonly ILogger<TradingEngine> _logger;

    public TradingEngine(
        TradingDbContext context,
        IBroker broker,
        RiskManager riskManager,
        SymbolBuilder symbolBuilder,
        PaperTradingEngine paperEngine,
        ISettingsService settings,
        ILogger<TradingEngine> logger)
    {
        _context = context;
        _broker = broker;
        _riskManager = riskManager;
        _symbolBuilder = symbolBuilder;
        _paperEngine = paperEngine;
        _settings = settings;
        _logger = logger;
    }

    public Task<bool> ValidateSignalAsync(ParsedSignal signal)
    {
        if (string.IsNullOrEmpty(signal.Index))
        {
            _logger.LogWarning("Invalid signal: missing index");
            return Task.FromResult(false);
        }
        if (signal.EntryPrice <= 0)
        {
            _logger.LogWarning("Invalid signal: invalid entry price");
            return Task.FromResult(false);
        }
        if (signal.StopLoss <= 0)
        {
            _logger.LogWarning("Invalid signal: invalid stop loss");
            return Task.FromResult(false);
        }
        // Safety: never let SL == Entry (would trigger stop-out immediately).
        // If they match (from PAID or an editorial error), buffer SL by 50 pts below entry.
        if (signal.StopLoss == signal.EntryPrice)
        {
            var buffered = Math.Max(0.05m, signal.EntryPrice - 50m);
            _logger.LogInformation(
                "SL equals Entry ({Entry}); applying default 50-pt buffer → SL={Sl}",
                signal.EntryPrice, buffered);
            signal.StopLoss = buffered;
        }
        if (signal.Targets.Count == 0)
        {
            _logger.LogWarning("Invalid signal: no targets");
            return Task.FromResult(false);
        }
        if (signal.ExpiryDate != default && signal.ExpiryDate < DateTime.Today)
        {
            _logger.LogWarning("Invalid signal: expiry date in past ({Expiry})", signal.ExpiryDate);
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    /// <summary>
    /// Maximum age (measured from the Telegram signal time) at which we will
    /// still place an order. Signals older than this are considered stale —
    /// the market has usually moved past the entry price and executing them
    /// risks a bad fill. Applies to auto and manual execution alike.
    /// </summary>
    private static readonly TimeSpan MaxSignalAge = TimeSpan.FromMinutes(10);

    public async Task<bool> ExecuteSignalAsync(ParsedSignal signal, int? accountId = null)
    {
        TradingSignal? failureSignalEntity = null;
        try
        {
            if (!await ValidateSignalAsync(signal))
            {
                await RecordFailureAsync(signal, failureSignalEntity, null, "Signal failed validation (missing index/entry/SL/targets or expired).");
                return false;
            }

            // Staleness guard: reject any signal older than 10 minutes so we
            // don't chase price that has already moved. SignalTime is populated
            // from the Telegram message timestamp by SignalParser.
            if (signal.SignalTime != default)
            {
                var age = DateTime.UtcNow - signal.SignalTime.ToUniversalTime();
                if (age > MaxSignalAge)
                {
                    var reason =
                        $"Signal is {age.TotalMinutes:0.0} min old (> {MaxSignalAge.TotalMinutes:0} min cutoff); refusing to execute stale signal.";
                    _logger.LogWarning(
                        "Skipping stale signal for {Index} {Strike} {Type}: age={AgeMin:0.0} min",
                        signal.Index, signal.Strike, signal.OptionType, age.TotalMinutes);
                    await RecordFailureAsync(signal, failureSignalEntity, null, reason);
                    return false;
                }
            }

            var account = accountId.HasValue
                ? await _context.TradingAccounts.FindAsync(accountId.Value)
                : await _context.TradingAccounts.FirstOrDefaultAsync(a => a.IsDefault && a.IsEnabled)
                  ?? await _context.TradingAccounts.FirstOrDefaultAsync(a => a.IsEnabled);

            if (account is null)
            {
                _logger.LogWarning("No trading account available for execution");
                await RecordFailureAsync(signal, failureSignalEntity, null, "No enabled trading account is configured.");
                return false;
            }

            if (!await _riskManager.ValidateLimitsAsync(account))
            {
                _logger.LogWarning("Risk limits exceeded for account {Id}", account.Id);
                await RecordFailureAsync(signal, failureSignalEntity, account.Id, $"Risk limits exceeded for account {account.Name}.");
                return false;
            }

            var duplicate = await _context.TradingSignals.AsNoTracking().AnyAsync(s =>
                s.Index == signal.Index &&
                s.Strike == signal.Strike &&
                s.OptionType == signal.OptionType &&
                s.Action == signal.Action &&
                s.Status == SignalStatus.Executed &&
                s.ReceivedTimestamp > DateTime.UtcNow.AddHours(-1));
            if (duplicate)
            {
                _logger.LogWarning("Duplicate signal executed within last hour, skipping");
                await RecordFailureAsync(signal, failureSignalEntity, account.Id, "Duplicate signal — same contract already executed in the last hour.");
                return false;
            }

            var resolved = await _symbolBuilder.ResolveAsync(signal,
                signal.ExpiryDate != default ? signal.ExpiryDate : (DateTime?)null);

            // Lot resolution priority: manual UI override → per-index setting → account default
            decimal lots;
            if (signal.Lots > 0)
            {
                lots = signal.Lots;
            }
            else
            {
                var indexKey = $"Lots.{signal.Index.ToUpperInvariant()}";
                var indexLots = await _settings.GetSettingAsync<int?>(indexKey);
                lots = (indexLots.HasValue && indexLots.Value > 0)
                    ? indexLots.Value
                    : Math.Max(1m, account.DefaultQuantity);
            }
            var quantity = lots * resolved.LotSize;
            _logger.LogInformation("Order size: {Lots} lot(s) × {LotSize} = {Qty} qty for {Index}", lots, resolved.LotSize, quantity, signal.Index);

            var signalEntity = await _context.TradingSignals
                .FirstOrDefaultAsync(s =>
                    s.Index == signal.Index && s.Strike == signal.Strike &&
                    s.OptionType == signal.OptionType && s.Action == signal.Action &&
                    s.ReceivedTimestamp > DateTime.UtcNow.AddMinutes(-10));

            if (signalEntity is null)
            {
                signalEntity = new TradingSignal
                {
                    OriginalMessage = signal.OriginalMessage,
                    TelegramTimestamp = signal.SignalTime,
                    ReceivedTimestamp = DateTime.UtcNow,
                    ProcessedTimestamp = DateTime.UtcNow,
                    Action = signal.Action,
                    Index = signal.Index,
                    Strike = signal.Strike,
                    OptionType = signal.OptionType,
                    EntryPrice = signal.EntryPrice,
                    StopLoss = signal.StopLoss,
                    Targets = signal.Targets,
                    ExpiryDate = signal.ExpiryDate == default ? resolved.Expiry : signal.ExpiryDate,
                    Status = SignalStatus.Pending,
                    Symbol = resolved.Symbol
                };
                _context.TradingSignals.Add(signalEntity);
                await _context.SaveChangesAsync();
            }
            else
            {
                signalEntity.Symbol = resolved.Symbol;
            }
            failureSignalEntity = signalEntity;

            var side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell;
            var order = new Order
            {
                SignalId = signalEntity.Id,
                TradingAccountId = account.Id,
                Symbol = resolved.Symbol,
                Quantity = quantity,
                Price = signal.EntryPrice,
                Side = side,
                OrderType = OrderType.Limit,
                ProductType = ProductType.Nrml,
                Status = OrderStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            if (!await _riskManager.ValidateOrderAsync(order, account))
            {
                _logger.LogWarning("Order failed risk validation");
                order.Status = OrderStatus.Rejected;
                order.ErrorMessage = $"Risk validation failed (qty={quantity}, price={signal.EntryPrice}). Check quantity/price limits and market-hours setting.";
                _context.Orders.Add(order);
                signalEntity.Status = SignalStatus.Failed;
                await _context.SaveChangesAsync();
                return false;
            }

            var paperMode = await _settings.GetSettingAsync<string>("PaperTrading");
            var isPaper = string.Equals(paperMode, "true", StringComparison.OrdinalIgnoreCase);

            if (isPaper)
            {
                _context.Orders.Add(order);
                await _context.SaveChangesAsync();
                var ok = await _paperEngine.SimulateOrderAsync(order, signal.EntryPrice);
                signalEntity.Status = ok ? SignalStatus.Executed : SignalStatus.Failed;
                await _context.SaveChangesAsync();
                return ok;
            }

            var response = await _broker.PlaceOrderAsync(new BrokerOrderRequest
            {
                Symbol = resolved.Symbol,
                SymbolToken = !string.IsNullOrWhiteSpace(resolved.SymbolToken)
                    ? resolved.SymbolToken
                    : (resolved.Token > 0 ? resolved.Token.ToString(CultureInfo.InvariantCulture) : null),
                Exchange = resolved.Exchange,
                Quantity = quantity,
                Price = signal.EntryPrice,
                Side = side,
                OrderType = OrderType.Limit,
                ProductType = ProductType.Nrml
            });

            if (!response.Success)
            {
                order.Status = OrderStatus.Rejected;
                order.ErrorMessage = response.ErrorMessage;
                _context.Orders.Add(order);
                signalEntity.Status = SignalStatus.Failed;
                await _context.SaveChangesAsync();
                _logger.LogError("Broker rejected order: {Error}", response.ErrorMessage);
                return false;
            }

            order.BrokerId = response.OrderId;
            order.Status = OrderStatus.Accepted;
            _context.Orders.Add(order);
            signalEntity.Status = SignalStatus.Executed;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order placed successfully: broker id {BrokerId}", response.OrderId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing signal");
            try
            {
                await RecordFailureAsync(signal, failureSignalEntity, accountId, $"Execution error: {ex.Message}");
            }
            catch { /* best-effort */ }
            return false;
        }
    }

    /// <summary>
    /// Writes a rejected Order row (and marks the source signal as Failed) so the UI can display
    /// a specific reason when execution stops before broker placement (validation, risk, duplicate, etc.).
    /// </summary>
    private async Task RecordFailureAsync(ParsedSignal signal, TradingSignal? signalEntity, int? accountId, string reason)
    {
        try
        {
            signalEntity ??= await _context.TradingSignals
                .OrderByDescending(s => s.ReceivedTimestamp)
                .FirstOrDefaultAsync(s =>
                    s.Index == signal.Index &&
                    s.Strike == signal.Strike &&
                    s.OptionType == signal.OptionType &&
                    s.Action == signal.Action &&
                    s.ReceivedTimestamp > DateTime.UtcNow.AddMinutes(-30));

            if (signalEntity is null)
                return;

            signalEntity.Status = SignalStatus.Failed;

            // Ensure the signal row exists (has an Id) before we point an Order FK at it.
            if (signalEntity.Id == 0)
            {
                await _context.SaveChangesAsync();
            }

            // Resolve a real TradingAccountId — Orders.TradingAccountId is a NOT NULL FK.
            int? resolvedAccountId = null;
            if (accountId.HasValue && accountId.Value > 0)
            {
                var exists = await _context.TradingAccounts
                    .AsNoTracking()
                    .AnyAsync(a => a.Id == accountId.Value);
                if (exists) resolvedAccountId = accountId.Value;
            }

            if (!resolvedAccountId.HasValue)
            {
                var fallback = await _context.TradingAccounts
                    .AsNoTracking()
                    .Where(a => a.IsEnabled)
                    .OrderByDescending(a => a.IsDefault)
                    .Select(a => (int?)a.Id)
                    .FirstOrDefaultAsync()
                    ?? await _context.TradingAccounts
                        .AsNoTracking()
                        .Select(a => (int?)a.Id)
                        .FirstOrDefaultAsync();
                resolvedAccountId = fallback;
            }

            if (resolvedAccountId.HasValue)
            {
                _context.Orders.Add(new Order
                {
                    SignalId = signalEntity.Id,
                    TradingAccountId = resolvedAccountId.Value,
                    Symbol = signalEntity.Symbol ?? string.Empty,
                    Quantity = 0,
                    Price = signal.EntryPrice,
                    Side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell,
                    OrderType = OrderType.Limit,
                    ProductType = ProductType.Nrml,
                    Status = OrderStatus.Rejected,
                    ErrorMessage = reason,
                    CreatedAt = DateTime.UtcNow
                });
            }
            else
            {
                _logger.LogWarning("Failure recorded without Order row (no trading account exists): {Reason}", reason);
            }

            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist failure reason");
        }
    }

    public async Task<bool> UpdatePositionsAsync()
    {
        try
        {
            if (!_broker.IsConnected)
            {
                _logger.LogDebug("Broker not connected; skipping position update");
                return false;
            }

            var positions = await _broker.GetPositionBookAsync();
            foreach (var brokerPosition in positions)
            {
                var position = await _context.Positions
                    .FirstOrDefaultAsync(p => p.Symbol == brokerPosition.Symbol && p.ClosedAt == null);

                if (position is not null)
                {
                    position.CurrentPrice = brokerPosition.CurrentPrice;
                    position.UnrealizedPnL = (brokerPosition.CurrentPrice - position.EntryPrice) * position.Quantity;
                    position.UnrealizedPnLPercentage = position.EntryPrice > 0
                        ? (position.UnrealizedPnL / (position.EntryPrice * position.Quantity)) * 100m
                        : 0m;
                }
            }

            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating positions");
            return false;
        }
    }

    public async Task<bool> CheckRiskLimitsAsync(int accountId)
    {
        var account = await _context.TradingAccounts.FindAsync(accountId);
        if (account is null) return false;
        return await _riskManager.ValidateLimitsAsync(account);
    }
}
