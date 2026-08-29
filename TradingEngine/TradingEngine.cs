using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Models;
using NexusApp.Helpers;
using NexusApp.Hubs;

namespace NexusApp.TradingEngine;

/// <summary>
/// Core trading engine for executing trades. Handles validation, risk checks,
/// symbol resolution, duplicate detection and routing between paper and live modes.
/// </summary>
public class TradingEngine(
    TradingDbContext context,
    IBroker broker,
    RiskManager riskManager,
    SymbolBuilder symbolBuilder,
    PaperTradingEngine paperEngine,
    ISettingsService settings,
    INotificationService notifications,
    ILogger<TradingEngine> logger,
    IHubContext<TradingHub> hub,
    IServiceProvider serviceProvider) : ITradingEngine
{
    private readonly TradingDbContext _context = context;
    private readonly IBroker _broker = broker;
    private readonly RiskManager _riskManager = riskManager;
    private readonly SymbolBuilder _symbolBuilder = symbolBuilder;
    private readonly PaperTradingEngine _paperEngine = paperEngine;
    private readonly ISettingsService _settings = settings;
    private readonly INotificationService _notifications = notifications;
    private readonly ILogger<TradingEngine> _logger = logger;
    private readonly IHubContext<TradingHub> _hub = hub;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

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
                var age = DateTime.UtcNow - signal.SignalTime.EnsureUtc();
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

            // Signals-by-time guard: only execute signals within the configured
            // IST trading window (e.g. 09:20–15:00). Blocks pre-open / late
            // signals that fall outside allowed trading hours.
            var timeWindowEnabled = await _settings.GetSettingAsync<bool?>("SignalTimeWindowEnabled") ?? false;
            if (timeWindowEnabled)
            {
                var startStr = await _settings.GetSettingAsync<string>("SignalTimeWindowStart");
                var endStr = await _settings.GetSettingAsync<string>("SignalTimeWindowEnd");
                if (TimeSpan.TryParse(startStr, CultureInfo.InvariantCulture, out var windowStart) &&
                    TimeSpan.TryParse(endStr, CultureInfo.InvariantCulture, out var windowEnd) &&
                    windowEnd > windowStart)
                {
                    var nowIst = DateTime.UtcNow.ToIst().TimeOfDay;
                    if (nowIst < windowStart || nowIst > windowEnd)
                    {
                        var reason =
                            $"Signal received at {nowIst:hh\\:mm} IST is outside the allowed trading window ({windowStart:hh\\:mm}–{windowEnd:hh\\:mm} IST); skipping.";
                        _logger.LogWarning(
                            "Skipping out-of-window signal for {Index} {Strike} {Type}: now={NowIst:hh\\:mm} IST window={Start:hh\\:mm}-{End:hh\\:mm}",
                            signal.Index, signal.Strike, signal.OptionType, nowIst, windowStart, windowEnd);
                        await RecordFailureAsync(signal, failureSignalEntity, null, reason);
                        return false;
                    }
                }
            }

            if (accountId.HasValue)
            {
                var account = await _context.TradingAccounts.FindAsync(accountId.Value);
                if (account is null)
                {
                    _logger.LogWarning("Trading account {AccountId} not found", accountId.Value);
                    await RecordFailureAsync(signal, failureSignalEntity, null, $"Account {accountId.Value} not found.");
                    return false;
                }
                if (!account.IsEnabled)
                {
                    _logger.LogWarning("Trading account {AccountId} is disabled", account.Id);
                    await RecordFailureAsync(signal, failureSignalEntity, account.Id, $"Account {account.Name} is disabled.");
                    return false;
                }
                return await ExecuteSignalForAccountAsync(signal, account);
            }
            else
            {
                var accounts = await _context.TradingAccounts.Where(a => a.IsEnabled).ToListAsync();
                if (accounts.Count == 0)
                {
                    _logger.LogWarning("No enabled trading accounts available for execution");
                    await RecordFailureAsync(signal, failureSignalEntity, null, "No enabled trading account is configured.");
                    return false;
                }

                _logger.LogInformation("Routing signal to {Count} enabled account(s)", accounts.Count);
                var executedAny = false;
                foreach (var acc in accounts)
                {
                    var ok = await ExecuteSignalForAccountAsync(signal, acc);
                    if (ok) executedAny = true;
                }
                return executedAny;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ExecuteSignalAsync");
            return false;
        }
    }

    private async Task<bool> ExecuteSignalForAccountAsync(ParsedSignal signal, TradingAccount account)
    {
        TradingSignal? failureSignalEntity = null;
        try
        {
            var (riskValid, riskReason) = await _riskManager.ValidateLimitsAsync(account);
            if (!riskValid)
            {
                _logger.LogWarning("Risk limit check failed for account {Name}: {Reason}", account.Name, riskReason);
                await RecordFailureAsync(signal, failureSignalEntity, account.Id, $"Risk limit check failed for {account.Name}: {riskReason}");
                return false;
            }

            // Find current signal entity if it exists already, to avoid matching itself in duplicate check
            var existingSignal = await _context.TradingSignals
                .AsNoTracking()
                .FirstOrDefaultAsync(s =>
                    s.Index == signal.Index && s.Strike == signal.Strike &&
                    s.OptionType == signal.OptionType && s.Action == signal.Action &&
                    s.EntryPrice == signal.EntryPrice && s.StopLoss == signal.StopLoss &&
                    s.ChannelName == signal.ChannelName &&
                    s.ReceivedTimestamp > DateTime.UtcNow.AddMinutes(-10));

            var currentSignalId = existingSignal?.Id ?? 0;

            var duplicateCooldownMinutes = await _settings.GetSettingAsync<int?>("DuplicateSignalCooldownMinutes") ?? 2;
            var duplicateOrder = false;
            var duplicatePriorSignal = false;

            if (duplicateCooldownMinutes > 0)
            {
                var cutoffUtc = DateTime.UtcNow.AddMinutes(-duplicateCooldownMinutes);
                var existingSymbol = existingSignal is { Symbol.Length: > 0 }
                    ? existingSignal.Symbol
                    : signal.Index;

                duplicateOrder = await _context.Orders.AsNoTracking().AnyAsync(o =>
                    o.TradingAccountId == account.Id &&
                    o.Symbol == existingSymbol &&
                    o.Signal.ChannelName == signal.ChannelName &&
                    (currentSignalId == 0 || o.SignalId != currentSignalId) &&
                    (o.Status == OrderStatus.Executed || o.Status == OrderStatus.Accepted) &&
                    o.CreatedAt > cutoffUtc);

                duplicatePriorSignal = await _context.TradingSignals.AsNoTracking().AnyAsync(s =>
                    (currentSignalId == 0 || s.Id != currentSignalId) &&
                    s.Index == signal.Index &&
                    s.Strike == signal.Strike &&
                    s.OptionType == signal.OptionType &&
                    s.Action == signal.Action &&
                    s.ChannelName == signal.ChannelName &&
                    s.Status == SignalStatus.Executed &&
                    s.ReceivedTimestamp > cutoffUtc);

                if (duplicateOrder || duplicatePriorSignal)
                {
                    _logger.LogWarning(
                        "Duplicate signal executed within the last {CooldownMinutes} minute(s) for account {AccountName}, skipping",
                        duplicateCooldownMinutes, account.Name);
                    await RecordFailureAsync(
                        signal,
                        existingSignal,
                        account.Id,
                        $"Duplicate signal — same contract from the same channel already executed in the last {duplicateCooldownMinutes} minute(s).");
                    return false;
                }
            }

            var resolved = await _symbolBuilder.ResolveAsync(signal,
                signal.ExpiryDate != default ? signal.ExpiryDate : (DateTime?)null);

            // Lot resolution priority: manual UI override → per-index setting → global default → account default
            decimal lots;
            if (signal.Lots > 0)
            {
                lots = signal.Lots;
            }
            else
            {
                var indexKey = $"Lots.{signal.Index.ToUpperInvariant()}";
                var indexLots = await _settings.GetSettingAsync<int?>(indexKey);
                if (indexLots.HasValue && indexLots.Value > 0)
                {
                    lots = indexLots.Value;
                }
                else
                {
                    var defaultLots = await _settings.GetSettingAsync<decimal?>("DefaultQuantity");
                    lots = defaultLots is > 0
                        ? defaultLots.Value
                        : Math.Max(1m, account.DefaultQuantity);
                }
            }
            var quantity = lots * resolved.LotSize;
            _logger.LogInformation("Order size for account {AccountName}: {Lots} lot(s) × {LotSize} = {Qty} qty for {Index}",
                account.Name, lots, resolved.LotSize, quantity, signal.Index);

            var signalEntity = await _context.TradingSignals
                .FirstOrDefaultAsync(s =>
                    s.Index == signal.Index && s.Strike == signal.Strike &&
                    s.OptionType == signal.OptionType && s.Action == signal.Action &&
                    s.EntryPrice == signal.EntryPrice && s.StopLoss == signal.StopLoss &&
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
                    Symbol = resolved.Symbol,
                    ChannelName = signal.ChannelName
                };
                _context.TradingSignals.Add(signalEntity);
                await _context.SaveChangesAsync();
            }
            else
            {
                signalEntity.Symbol = resolved.Symbol;
                // Refresh value-bearing fields so this order/position carries THIS signal's
                // own Entry/SL/Targets rather than inheriting a prior signal's values.
                signalEntity.EntryPrice = signal.EntryPrice;
                signalEntity.StopLoss = signal.StopLoss;
                signalEntity.Targets = signal.Targets;
                signalEntity.ChannelName = signal.ChannelName;
            }
            failureSignalEntity = signalEntity;

            // Read Entry & Target price offsets and StopLoss Buffer
            var entryOffsetSetting = await _settings.GetSettingAsync<decimal?>("EntryPriceOffset");
            var targetOffsetSetting = await _settings.GetSettingAsync<decimal?>("TargetPriceOffset");
            var stopLossBufferSetting = await _settings.GetSettingAsync<decimal?>("StopLossBuffer");
            var entryOffset = entryOffsetSetting ?? 0m;
            var targetOffset = targetOffsetSetting ?? 0m;
            var stopLossBuffer = stopLossBufferSetting ?? 0m;

            // Read order variety (Robo = bracket order with SL+Target, Limit = plain limit,
            // Market = execute entry/exit/SL at market price for fastest fills)
            var orderVariety = await _settings.GetSettingAsync<string>("OrderVariety") ?? "Robo";
            var isMarket = string.Equals(orderVariety, "Market", StringComparison.OrdinalIgnoreCase);
            var isRobo = !isMarket && string.Equals(orderVariety, "Robo", StringComparison.OrdinalIgnoreCase);
            var entryOrderType = isMarket ? OrderType.Market : OrderType.Limit;
            var trailingSLPoints = await _settings.GetSettingAsync<decimal?>("TrailingStopLossPoints") ?? 0m;

            var limitPrice = signal.EntryPrice;
            // Market orders fill at the prevailing market price, so the entry-price
            // limit offset does not apply to them.
            if (!isMarket && entryOffset > 0 && signal.Action == SignalAction.Buy)
            {
                limitPrice = Math.Max(0.05m, signal.EntryPrice - entryOffset);
                _logger.LogInformation(
                    "Applied Entry Price offset (-{Offset}): Raw Entry={RawEntry} → Limit Order Price={LimitPrice}",
                    entryOffset, signal.EntryPrice, limitPrice);
            }

            if (stopLossBuffer > 0 && signalEntity.StopLoss > 0)
            {
                var bufferedSl = Math.Max(0.05m, signalEntity.StopLoss - stopLossBuffer);
                _logger.LogInformation(
                    "Applied StopLoss Buffer (-{Buffer}): Raw SL={RawSL} → Buffered SL={BufferedSL}",
                    stopLossBuffer, signalEntity.StopLoss, bufferedSl);
                signalEntity.StopLoss = bufferedSl;
                await _context.SaveChangesAsync();
            }

            if (targetOffset > 0 && signalEntity.Targets.Count > 0)
            {
                var adjustedTargets = signalEntity.Targets
                    .Select(t => Math.Max(limitPrice + 1m, t - targetOffset))
                    .ToList();
                signalEntity.Targets = adjustedTargets;
                _logger.LogInformation(
                    "Applied Target Price offset (-{Offset}): Adjusted Targets=[{Targets}]",
                    targetOffset, string.Join(", ", adjustedTargets));
                await _context.SaveChangesAsync();
            }

            var side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell;

            // All broker orders are placed as intraday (MIS). Robo/bracket orders are sent
            // as BO by the broker adapter and are tagged with Mos so order sync can identify
            // broker-managed positions and skip local SL/target tracking.
            var productType = isRobo ? ProductType.Mos : ProductType.Mis;

            // Compute Robo point offsets from signal SL and first target
            decimal squareOffPts = 0m, stopLossPts = 0m;
            if (isRobo)
            {
                // Target offset: distance from entry to the first target
                if (signalEntity.Targets.Count > 0)
                    squareOffPts = Math.Abs(signalEntity.Targets[0] - limitPrice);

                // SL offset: distance from entry to stop-loss
                if (signalEntity.StopLoss > 0)
                    stopLossPts = Math.Abs(limitPrice - signalEntity.StopLoss);

                // Guard: ensure both offsets are meaningful
                if (squareOffPts <= 0 || stopLossPts <= 0)
                {
                    _logger.LogWarning(
                        "Robo order offsets invalid (squareoff={SqOff}, stoploss={SL}); falling back to Limit order",
                        squareOffPts, stopLossPts);
                    isRobo = false;
                    productType = ProductType.Mis;
                }
                else
                {
                    _logger.LogInformation(
                        "Robo order offsets: squareoff={SqOff} pts, stoploss={SL} pts, trailingSL={TSL} pts",
                        squareOffPts, stopLossPts, trailingSLPoints);
                }
            }

            var order = new Order
            {
                SignalId = signalEntity.Id,
                TradingAccountId = account.Id,
                Symbol = resolved.Symbol,
                Quantity = quantity,
                Price = limitPrice,
                Side = side,
                OrderType = entryOrderType,
                ProductType = productType,
                Status = OrderStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            if (!await _riskManager.ValidateOrderAsync(order, account))
            {
                _logger.LogWarning("Order failed risk validation for account {AccountName}", account.Name);
                order.Status = OrderStatus.Rejected;
                if (string.IsNullOrWhiteSpace(order.ErrorMessage))
                {
                    order.ErrorMessage = $"Risk validation failed (qty={quantity}, price={limitPrice}). Check quantity/price limits and market-hours setting.";
                }
                _context.Orders.Add(order);
                if (signalEntity.Status != SignalStatus.Executed)
                {
                    signalEntity.Status = SignalStatus.Failed;
                }
                await _context.SaveChangesAsync();
                order.BrokerId = $"REJ-{order.Id}";
                await _context.SaveChangesAsync();
                return false;
            }

            var isPaper = account.IsPaperAccount;

            if (isPaper)
            {
                _context.Orders.Add(order);
                await _context.SaveChangesAsync();
                order.BrokerId = "Paper Account";

                // Paper orders are simulated as fills at their configured limit price.
                // This includes EntryPriceOffset, so the simulated entry remains aligned
                // with the order shown in the Watchlist and Orders grids.
                // Market orders instead fill at the prevailing live price (CMP) to mirror
                // a real market execution.
                var fillPrice = limitPrice;
                if (isMarket)
                {
                    try
                    {
                        var quoteBroker = _serviceProvider.GetRequiredKeyedService<IBroker>(account.BrokerType);
                        var cmp = await quoteBroker.GetLiveQuoteAsync(resolved.Symbol);
                        if (cmp > 0)
                            fillPrice = cmp;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Paper market order: failed to fetch CMP for {Symbol}; falling back to signal entry price {Price}",
                            resolved.Symbol, limitPrice);
                    }
                }
                _logger.LogInformation(
                    "Paper order filled at {Price} for {Symbol} ({Mode})",
                    fillPrice, resolved.Symbol, isMarket ? "Market" : "Limit");

                var ok = await _paperEngine.SimulateOrderAsync(order, fillPrice);
                if (ok)
                {
                    signalEntity.Status = SignalStatus.Executed;
                    await _notifications.SendTelegramOrderPlacedAsync(account.Name, true, resolved.Symbol, signal.Action.ToString(), fillPrice, quantity, order.BrokerId, "EXECUTED (Paper)");
                }
                else if (signalEntity.Status != SignalStatus.Executed)
                {
                    signalEntity.Status = SignalStatus.Failed;
                }
                await _context.SaveChangesAsync();
                return ok;
            }

            // Resolve the specific keyed broker instance for the account type
            var accountBroker = _serviceProvider.GetRequiredKeyedService<IBroker>(account.BrokerType);

            string? symbolToken;
            if (!string.IsNullOrWhiteSpace(resolved.SymbolToken))
                symbolToken = resolved.SymbolToken;
            else if (resolved.Token > 0)
                symbolToken = resolved.Token.ToString(CultureInfo.InvariantCulture);
            else
                symbolToken = null;

            var brokerRequest = new BrokerOrderRequest
            {
                Symbol = resolved.Symbol,
                SymbolToken = symbolToken,
                Exchange = resolved.Exchange,
                Quantity = quantity,
                Price = limitPrice,
                Side = side,
                OrderType = entryOrderType,
                ProductType = productType,
                Variety = isRobo ? "ROBO" : "NORMAL",
                SquareOffPoints = squareOffPts,
                StopLossPoints = stopLossPts,
                TrailingStopLossPoints = trailingSLPoints
            };

            _logger.LogInformation(
                "Placing {Variety} order for {Symbol} on {Broker}: entry={Price}, SqOff={SqOff}, SL={SL}",
                brokerRequest.Variety, resolved.Symbol, account.BrokerType,
                limitPrice, squareOffPts, stopLossPts);

            // Slippage guard: before sending a live order, compare the current
            // market price (CMP) against the signal entry price. If the market
            // has moved adversely beyond the configured percentage, reject the
            // order to avoid a bad fill.
            var slippageGuardEnabled = await _settings.GetSettingAsync<bool?>("SlippageGuardEnabled") ?? false;
            if (slippageGuardEnabled && signal.EntryPrice > 0)
            {
                var maxSlippagePercent = await _settings.GetSettingAsync<decimal?>("MaxSlippagePercent") ?? 1.0m;
                if (maxSlippagePercent > 0)
                {
                    decimal cmp = 0m;
                    try
                    {
                        cmp = await accountBroker.GetLiveQuoteAsync(resolved.Symbol);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Slippage guard: failed to fetch CMP for {Symbol}; proceeding without check", resolved.Symbol);
                    }

                    if (cmp > 0)
                    {
                        // Adverse move: for a Buy, price rising above entry; for a Sell, price falling below entry.
                        var adverseMove = side == OrderSide.Buy
                            ? cmp - signal.EntryPrice
                            : signal.EntryPrice - cmp;
                        var slippagePercent = adverseMove / signal.EntryPrice * 100m;
                        if (slippagePercent > maxSlippagePercent)
                        {
                            var reason =
                                $"Slippage guard: CMP {cmp} vs entry {signal.EntryPrice} = {slippagePercent:0.00}% adverse (> {maxSlippagePercent:0.00}% limit); rejecting order.";
                            _logger.LogWarning(
                                "Slippage guard rejected {Symbol} on {Broker}: CMP={Cmp} entry={Entry} slip={Slip:0.00}% limit={Limit:0.00}%",
                                resolved.Symbol, account.BrokerType, cmp, signal.EntryPrice, slippagePercent, maxSlippagePercent);
                            order.Status = OrderStatus.Rejected;
                            order.ErrorMessage = reason;
                            _context.Orders.Add(order);
                            if (signalEntity.Status != SignalStatus.Executed)
                            {
                                signalEntity.Status = SignalStatus.Failed;
                            }
                            await _context.SaveChangesAsync();
                            order.BrokerId = $"REJ-{order.Id}";
                            await _context.SaveChangesAsync();
                            return false;
                        }

                        _logger.LogInformation(
                            "Slippage guard OK for {Symbol}: CMP={Cmp} entry={Entry} slip={Slip:0.00}% (limit {Limit:0.00}%)",
                            resolved.Symbol, cmp, signal.EntryPrice, slippagePercent, maxSlippagePercent);
                    }
                }
            }

            var response = await accountBroker.PlaceOrderAsync(brokerRequest);

            if (!response.Success)
            {
                order.Status = OrderStatus.Rejected;
                order.ErrorMessage = response.ErrorMessage;
                _context.Orders.Add(order);
                if (signalEntity.Status != SignalStatus.Executed)
                {
                    signalEntity.Status = SignalStatus.Failed;
                }
                await _context.SaveChangesAsync();
                order.BrokerId = $"REJ-{order.Id}";
                await _context.SaveChangesAsync();
                _logger.LogError("Broker {BrokerType} rejected order for account {AccountName}: {Error}",
                    account.BrokerType, account.Name, response.ErrorMessage);
                return false;
            }

            order.BrokerId = response.OrderId;
            order.Status = OrderStatus.Accepted;
            _context.Orders.Add(order);
            signalEntity.Status = SignalStatus.Executed;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order placed successfully on broker {BrokerType} for account {AccountName}: broker id {BrokerId}",
                account.BrokerType, account.Name, response.OrderId);

            await _notifications.SendTelegramOrderPlacedAsync(
                account.Name,
                false,
                resolved.Symbol,
                signal.Action.ToString(),
                limitPrice,
                quantity,
                response.OrderId,
                "ACCEPTED");

            await _notifications.SendOrderNotificationAsync(
                resolved.Symbol,
                signal.Action.ToString(),
                $"Order submitted to {account.Name} ({account.BrokerType}). Order ID: {response.OrderId} | Status: ACCEPTED");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing signal for account {AccountName}", account.Name);
            try
            {
                await RecordFailureAsync(signal, failureSignalEntity, account.Id, $"Execution error: {DescribeException(ex)}");
            }
            catch { /* best-effort */ }
            return false;
        }
    }

    /// <summary>
    /// Flattens an exception chain into a single readable message. Framework
    /// wrappers like DbUpdateException only expose a generic "See the inner
    /// exception for details" message, so the real cause (e.g. "SQLite Error 5:
    /// database is locked") lives in InnerException. This surfaces it to the UI.
    /// </summary>
    private static string DescribeException(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var msg = current.Message?.Trim();
            if (!string.IsNullOrEmpty(msg) && !messages.Contains(msg))
                messages.Add(msg);
        }
        return string.Join(" → ", messages);
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

            if (signalEntity.Status != SignalStatus.Executed)
            {
                signalEntity.Status = SignalStatus.Failed;
            }

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
                var rejOrder = new Order
                {
                    SignalId = signalEntity.Id,
                    TradingAccountId = resolvedAccountId.Value,
                    Symbol = signalEntity.Symbol ?? string.Empty,
                    Quantity = 0,
                    Price = signal.EntryPrice,
                    Side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell,
                    OrderType = OrderType.Limit,
                    ProductType = ProductType.Nrml,
                    Status = OrderStatus.Failed,
                    ErrorMessage = reason,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Orders.Add(rejOrder);
                await _context.SaveChangesAsync();
                rejOrder.BrokerId = $"REJ-{rejOrder.Id}";
                await _context.SaveChangesAsync();
            }
            else
            {
                _logger.LogWarning("Failure recorded without Order row (no trading account exists): {Reason}", reason);
                await _context.SaveChangesAsync();
            }
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
                    position.UnrealizedPnL = PnlCalculator.UnrealizedPnl(position.EntryPrice, brokerPosition.CurrentPrice, position.Quantity);
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
        var (isValid, _) = await _riskManager.ValidateLimitsAsync(account);
        return isValid;
    }

    public async Task<bool> SquareOffPositionAsync(int positionId)
    {
        try
        {
            var position = await _context.Positions
                .Include(p => p.TradingAccount)
                .FirstOrDefaultAsync(p => p.Id == positionId && p.ClosedAt == null);

            if (position is null)
            {
                _logger.LogWarning("Position {PositionId} not found or already closed", positionId);
                return false;
            }

            var account = position.TradingAccount;
            if (account is null)
            {
                _logger.LogWarning("Account not found for position {PositionId}", positionId);
                return false;
            }

            // Determine the exit (LTP) price for the square-off.
            // The WebSocket-streamed CMP (position.CurrentPrice) is the source of truth
            // shown across the UI and is refreshed every ~1-2s. Angel's REST quote
            // endpoint is flaky and can return a stale/incorrect price, so it is only
            // used as a fallback when no streamed CMP is available.
            var ltp = position.CurrentPrice;

            if (ltp <= 0m)
            {
                try
                {
                    var accountBroker = _serviceProvider.GetRequiredKeyedService<IBroker>(account.BrokerType);
                    if (accountBroker.IsConnected)
                    {
                        ltp = await accountBroker.GetLiveQuoteAsync(position.Symbol);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get live quote for square-off of {Symbol}", position.Symbol);
                }
            }

            if (ltp <= 0m)
            {
                ltp = position.EntryPrice;
            }

            var isPaper = account.IsPaperAccount;

            if (isPaper)
            {
                var order = new Order
                {
                    SignalId = position.SignalId,
                    TradingAccountId = account.Id,
                    Symbol = position.Symbol,
                    Quantity = position.Quantity,
                    Price = ltp,
                    Side = OrderSide.Sell,
                    OrderType = OrderType.Market,
                    ProductType = ProductType.Nrml,
                    Status = OrderStatus.Executed,
                    CreatedAt = DateTime.UtcNow,
                    ExecutedAt = DateTime.UtcNow,
                    ExecutedPrice = ltp,
                    FilledQuantity = position.Quantity
                };
                _context.Orders.Add(order);

                position.ClosedAt = DateTime.UtcNow;
                position.ClosingPrice = ltp;
                position.RealizedPnL = PnlCalculator.RealizedPnl(position.EntryPrice, ltp, position.Quantity);
                position.UnrealizedPnL = 0;
                position.UnrealizedPnLPercentage = 0;

                await _context.SaveChangesAsync();
                _logger.LogInformation("Paper position {Symbol} squared off at {Price}", position.Symbol, ltp);
                await _notifications.SendTelegramOrderClosedAsync(account.Name, true, position.Symbol, position.EntryPrice, ltp, position.RealizedPnL.Value, position.Quantity, "Square Off");
                await _hub.Clients.All.SendAsync("PositionChanged", new { Timestamp = DateTime.UtcNow });
                await _hub.Clients.All.SendAsync("OrderStatusChanged", new { Timestamp = DateTime.UtcNow });
                return true;            }
            else
            {
                // Live broker square-off
                var accountBroker = _serviceProvider.GetRequiredKeyedService<IBroker>(account.BrokerType);

                // A resting broker-side target SELL LIMIT (placed for plain LIMIT entries)
                // must be pulled first, otherwise the exit below would sell the same
                // quantity twice. It is also a Pending/Accepted Sell order, so it would
                // otherwise trip the exitAlreadyPending guard.
                await RestingTargetOrders.CancelAsync(
                    _context, accountBroker, account.Id, position.Symbol, _logger);

                var exitAlreadyPending = await _context.Orders.AnyAsync(o =>
                    o.TradingAccountId == account.Id &&
                    o.Symbol == position.Symbol &&
                    o.Side == OrderSide.Sell &&
                    (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Accepted));
                if (exitAlreadyPending)
                {
                    _logger.LogInformation("Live square-off for {Symbol} is already pending", position.Symbol);
                    return true;
                }

                string? symbolToken = null;
                string? exchange = null;

                try
                {
                    var instrumentMaster = _serviceProvider.GetService<NexusApp.Brokers.AngelOne.AngelInstrumentMaster>();
                    if (instrumentMaster is not null)
                    {
                        await instrumentMaster.EnsureLoadedAsync();
                        var entry = instrumentMaster.FindByTradingSymbol(position.Symbol);
                        if (entry is not null)
                        {
                            symbolToken = entry.Token;
                            exchange = entry.ExchangeSegment;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Instrument master lookup failed for square-off {Symbol}; broker will fall back to cache", position.Symbol);
                }

                var response = await accountBroker.PlaceOrderAsync(new BrokerOrderRequest
                {
                    Symbol = position.Symbol,
                    SymbolToken = symbolToken,
                    Exchange = exchange,
                    Quantity = position.Quantity,
                    Side = OrderSide.Sell,
                    OrderType = OrderType.Market,
                    ProductType = ProductType.Mis
                });

                // Mark the local position closed as soon as the broker accepts the exit
                // order so open-position grids stop showing a position after its SL/target
                // has been hit. The broker order remains the source of truth for the
                // eventual fill record.
                var order = new Order
                {
                    SignalId = position.SignalId,
                    TradingAccountId = account.Id,
                    Symbol = position.Symbol,
                    Quantity = position.Quantity,
                    Price = ltp,
                    Side = OrderSide.Sell,
                    OrderType = OrderType.Market,
                    ProductType = ProductType.Mis,
                    Status = response.Success ? OrderStatus.Accepted : OrderStatus.Failed,
                    BrokerId = response.OrderId,
                    ErrorMessage = response.ErrorMessage,
                    CreatedAt = DateTime.UtcNow,
                    ExecutedAt = null,
                    ExecutedPrice = null,
                    FilledQuantity = null
                };
                _context.Orders.Add(order);

                if (response.Success)
                {
                    position.ClosedAt = DateTime.UtcNow;
                    position.ClosingPrice = ltp;
                    position.RealizedPnL = PnlCalculator.RealizedPnl(
                        position.EntryPrice, ltp, position.Quantity);
                    position.UnrealizedPnL = 0m;
                    position.UnrealizedPnLPercentage = 0m;
                }

                await _context.SaveChangesAsync();
                await _hub.Clients.All.SendAsync("PositionChanged", new { Timestamp = DateTime.UtcNow });
                await _hub.Clients.All.SendAsync("OrderStatusChanged", new { Timestamp = DateTime.UtcNow });
                if (response.Success)
                {
                    _logger.LogInformation("Live square-off order accepted for {Symbol}; position removed from open positions", position.Symbol);
                    return true;
                }
                else
                {
                    _logger.LogError("Failed to square off live position {Symbol}: {Error}", position.Symbol, response.ErrorMessage);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SquareOffPositionAsync for position {PositionId}", positionId);
            return false;
        }
    }
}
