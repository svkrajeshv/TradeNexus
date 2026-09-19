using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Helpers;
using NexusApp.Hubs;
using NexusApp.Interfaces;
using NexusApp.Models;
using NexusApp.Parser;
using NexusApp.Telegram;
using NexusApp.TradingEngine;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Background service that connects to Telegram, listens for signals in real time,
/// persists them, executes them according to the configured trading mode and pushes
/// updates to connected UI clients via SignalR.
/// Configuration comes from the DB-persisted <see cref="TelegramManager"/> so that
/// changes made in the UI take effect on the next reconnect without an app restart.
/// </summary>
public sealed partial class TelegramListenerService(
    ILogger<TelegramListenerService> logger,
    IConfiguration config,
    IServiceProvider serviceProvider,
    TelegramManager manager,
    IHubContext<TradingHub> hub) : BackgroundService
{
    private readonly ILogger<TelegramListenerService> _logger = logger;
    private readonly IConfiguration _config = config;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly TelegramManager _manager = manager;
    private readonly IHubContext<TradingHub> _hub = hub;
    /// <summary>
    /// Messages already handled, keyed by (channel, message id) with the message TEXT as the
    /// value. Telegram raises an update for edits under the SAME message id, so the text is
    /// kept to tell a genuine re-delivery (ignore) from an edited call (re-process).
    /// </summary>
    private readonly ConcurrentDictionary<(string Channel, long MessageId), string> _processedMessages = new();
    private readonly ConcurrentDictionary<string, int> _lastSeenMessageIds = new(StringComparer.OrdinalIgnoreCase);
    private const string SignalStatusChangedEvent = "SignalStatusChanged";

    /// <summary>
    /// How long after a call was received a further message for the same channel + contract is
    /// considered a revision of that call (edit / corrected re-post) rather than a new signal.
    /// </summary>
    private static readonly TimeSpan SignalRevisionWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after a call was received an untagged Target/SL-only message from the same
    /// channel is still treated as a continuation of that call. Some channels (e.g. "Vip
    /// Group") post the entry, the targets and the stop-loss as three consecutive messages
    /// without replying to the first one, all within a minute or two.
    /// </summary>
    private static readonly TimeSpan FollowUpWindow = TimeSpan.FromMinutes(10);

    private DateTime _serviceStartTime = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _serviceStartTime = DateTime.UtcNow;
        _manager.Client.MessageReceived += HandleIncomingAsync;

        var settings = await _manager.LoadAsync();
        MergeAppsettingsFallback(settings);
        if (!settings.Enabled
            && settings.ApiId > 0
            && !string.IsNullOrWhiteSpace(settings.ApiHash)
            && !string.IsNullOrWhiteSpace(settings.PhoneNumber))
        {
            settings.Enabled = true;
            await _manager.SaveAsync(settings);
            _logger.LogInformation("Auto-enabled Telegram listener at startup");
        }

        var reconnectDelay = TimeSpan.FromSeconds(10);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var current = _manager.Snapshot();
                if (!current.Enabled)
                {
                    _logger.LogDebug("Telegram listener idle (disabled)");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    await _manager.LoadAsync();
                    continue;
                }

                if (current.ApiId <= 0 || string.IsNullOrWhiteSpace(current.ApiHash) || string.IsNullOrWhiteSpace(current.PhoneNumber))
                {
                    _logger.LogWarning("Telegram credentials incomplete; waiting for configuration");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    await _manager.LoadAsync();
                    continue;
                }

                if (!_manager.IsConnected
                    && _manager.State != TelegramConnectionState.AwaitingCode
                    && _manager.State != TelegramConnectionState.AwaitingPassword
                    && _manager.State != TelegramConnectionState.Connecting)
                {
                    _logger.LogInformation("Connecting to Telegram (channels: {Channels})",
                        string.Join(", ", current.Channels));
                    await _manager.ConnectAsync();
                    _serviceStartTime = DateTime.UtcNow;
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                if (_manager.IsConnected && current.Channels.Count > 0)
                {
                    foreach (var channel in current.Channels)
                    {
                        var recent = await _manager.Client.PollChannelAsync(channel, 10);
                        var isFirstPoll = !_lastSeenMessageIds.ContainsKey(channel);

                        if (isFirstPoll && recent.Count > 0)
                        {
                            var maxId = recent.Max(m => (int)m.MessageId);
                            _lastSeenMessageIds[channel] = maxId;
                            _logger.LogInformation("Initialized last seen message ID for channel '{Channel}' to {MaxId}", channel, maxId);

                            // Only process messages received after startup (or within 2 minutes)
                            // to prevent reloading old historical data on startup.
                            foreach (var msg in recent)
                            {
                                if ((DateTime.UtcNow - msg.Timestamp).TotalMinutes <= 2)
                                {
                                    await HandleIncomingAsync(msg);
                                }
                            }
                        }
                        else
                        {
                            var lastSeenId = _lastSeenMessageIds.GetValueOrDefault(channel);
                            foreach (var msg in recent)
                            {
                                if ((int)msg.MessageId > lastSeenId)
                                {
                                    await HandleIncomingAsync(msg);
                                }
                            }
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                await _manager.LoadAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram listener loop error, retrying in {Delay}s", reconnectDelay.TotalSeconds);
                await SafeDelay(reconnectDelay, stoppingToken);
            }
        }

        _manager.Client.MessageReceived -= HandleIncomingAsync;
        await _manager.DisconnectAsync();
        _logger.LogInformation("Telegram listener stopped");
    }

    /// <summary>
    /// On first run, seed the manager with any values that happen to be present in
    /// appsettings.json so existing configurations continue to work. Persists them
    /// so subsequent restarts read from the DB.
    /// </summary>
    private void MergeAppsettingsFallback(TelegramSettings current)
    {
        var apiId = _config.GetValue<int>("Telegram:ApiId");
        var apiHash = _config.GetValue<string>("Telegram:ApiHash");
        var phone = _config.GetValue<string>("Telegram:PhoneNumber");
        var channel = _config.GetValue<string>("Telegram:ChannelName");
        var enabled = _config.GetValue("Telegram:Enabled", false);

        var changed = false;
        if (current.ApiId == 0 && apiId > 0) { current.ApiId = apiId; changed = true; }
        if (string.IsNullOrEmpty(current.ApiHash) && !string.IsNullOrWhiteSpace(apiHash)) { current.ApiHash = apiHash!; changed = true; }
        if (string.IsNullOrEmpty(current.PhoneNumber) && !string.IsNullOrWhiteSpace(phone)) { current.PhoneNumber = phone!; changed = true; }
        if (current.Channels.Count == 0 && !string.IsNullOrWhiteSpace(channel)) { current.Channels.Add(channel!); changed = true; }
        if (!current.Enabled && enabled) { current.Enabled = true; changed = true; }

        if (changed)
        {
            _logger.LogInformation("Seeding Telegram settings from appsettings.json (one-time migration)");
            _ = _manager.SaveAsync(current);
        }
    }

    /// <summary>
    /// Returns true only when the message's channel is present in the user's
    /// configured monitored-channels list. Matching is case-insensitive and tolerant
    /// of a leading '@' on either side. Because incoming messages are identified by
    /// channel <b>title</b> but users may have saved a <b>@username</b> (or vice-versa),
    /// the joined-channels list is used to treat the title and username of the same
    /// channel as equivalent.
    /// </summary>
    private bool IsMonitoredChannel(string? senderName)
    {
        if (string.IsNullOrWhiteSpace(senderName))
            return false;

        var monitored = _manager.Snapshot().Channels;
        if (monitored is null || monitored.Count == 0)
            return false;

        static string Normalize(string s) => s.Trim().TrimStart('@');
        var sender = Normalize(senderName);

        // Build the set of identifiers (title + username) that refer to the sender's
        // channel, so a monitored entry matches regardless of which form was saved.
        var senderAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sender };
        try
        {
            foreach (var ac in _manager.ListAvailableChannels())
            {
                var title = Normalize(ac.Title ?? string.Empty);
                var username = Normalize(ac.Username ?? string.Empty);
                if ((title.Length > 0 && string.Equals(title, sender, StringComparison.OrdinalIgnoreCase)) ||
                    (username.Length > 0 && string.Equals(username, sender, StringComparison.OrdinalIgnoreCase)))
                {
                    if (title.Length > 0) senderAliases.Add(title);
                    if (username.Length > 0) senderAliases.Add(username);
                }
            }
        }
        catch
        {
            // If the joined-channels list can't be read, fall back to plain title match.
        }

        return monitored.Any(c => senderAliases.Contains(Normalize(c)));
    }

    private async Task HandleIncomingAsync(TelegramMessage message)
    {
        // Only process messages that originate from an explicitly monitored channel.
        // The real-time MessageReceived event fires for EVERY channel the connected
        // Telegram account has joined, so without this guard signals from channels the
        // user never added (e.g. "venkat trading signals") would be parsed and traded.
        if (!IsMonitoredChannel(message.SenderName))
        {
            _logger.LogDebug(
                "Ignoring message {MessageId} from unmonitored channel '{Channel}'.",
                message.MessageId, message.SenderName);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.SenderName))
        {
            _lastSeenMessageIds.AddOrUpdate(
                message.SenderName,
                (int)message.MessageId,
                (key, oldVal) => Math.Max(oldVal, (int)message.MessageId)
            );
        }

        var messageKey = (message.SenderName, message.MessageId);
        var messageText = message.Text ?? string.Empty;
        if (!_processedMessages.TryAdd(messageKey, messageText))
        {
            if (_processedMessages.TryGetValue(messageKey, out var previousText) &&
                string.Equals(previousText, messageText, StringComparison.Ordinal))
            {
                // Same message delivered twice (real-time event + poll) — nothing to do.
                return;
            }

            // The channel edited the call (corrected entry/SL/targets); re-process so the
            // stored signal is refreshed instead of keeping the stale levels.
            _processedMessages[messageKey] = messageText;
        }

        if (_processedMessages.Count > 2000)
        {
            foreach (var key in _processedMessages.Keys.OrderBy(k => k.MessageId).Take(500).ToList())
                _processedMessages.TryRemove(key, out _);
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<SignalParserResolver>();
            var parser = resolver.Resolve(message.SenderName);
            var context = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

            if (await TryHandleIntermediateSquareOffAsync(context, message, scope.ServiceProvider))
                return;

            // Tagged follow-up that only carries Target/SL levels for a previously
            // posted call (e.g. "Vip Group" replies "Target 320/350" to its own signal).
            if (await TryHandleFollowUpUpdateAsync(context, message))
                return;

            // Channel-specific skip rule (e.g. ABC BTST TRADE positional "#BTST TRADE"
            // calls). Only intraday signals (BUY above entry) are traded.
            if (parser.ShouldSkip(message.Text) &&
                !(parser.RequiresActivation && parser.IsActivationMessage(message.Text)))
            {
                _logger.LogInformation(
                    "Skipping message {MessageId} from {Channel} — flagged by {Parser} as non-tradable.",
                    message.MessageId, message.SenderName, parser.GetType().Name);
                return;
            }

            // IGNORE command: cancel matching pending/accepted orders and block execution.
            if (IsIgnoreMessage(messageText))
            {
                var targetSignal = await FindIgnoreTargetSignalAsync(context, parser, message);
                if (targetSignal is null)
                {
                    _logger.LogInformation(
                        "IGNORE message {MessageId} received from {Channel}, but no matching active signal was found.",
                        message.MessageId, message.SenderName);
                    return;
                }

                var cancellableOrders = await context.Orders
                    .Include(o => o.TradingAccount)
                    .Where(o => o.SignalId == targetSignal.Id &&
                                (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Accepted))
                    .ToListAsync();

                foreach (var ord in cancellableOrders)
                {
                    var cancelled = false;
                    try
                    {
                        if (ord.TradingAccount is null || ord.TradingAccount.IsPaperAccount || string.IsNullOrWhiteSpace(ord.BrokerId))
                        {
                            cancelled = true;
                        }
                        else
                        {
                            var broker = scope.ServiceProvider.GetRequiredKeyedService<IBroker>(ord.TradingAccount.BrokerType);
                            cancelled = await broker.CancelOrderAsync(ord.BrokerId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed cancelling order {OrderId} for IGNORE signal {SignalId}", ord.Id, targetSignal.Id);
                    }

                    if (cancelled)
                    {
                        ord.Status = OrderStatus.Cancelled;
                        ord.ErrorMessage = "Cancelled due to IGNORE message";
                    }
                }

                // Mark ignored unless trade already completed; this blocks any pending execution path.
                if (targetSignal.Status != SignalStatus.Executed)
                {
                    targetSignal.Status = SignalStatus.Ignored;
                }

                await context.SaveChangesAsync();

                await _hub.Clients.All.SendAsync(SignalStatusChangedEvent, new
                {
                    targetSignal.Id,
                    Status = targetSignal.Status.ToString()
                });

                await _hub.Clients.All.SendAsync("OrderStatusChanged", new { Timestamp = DateTime.UtcNow });

                _logger.LogInformation(
                    "Signal {SignalId} ({Index} {Strike}{Type}) marked Ignored via message {MessageId}; cancelled {CancelledCount} pending/accepted order(s).",
                    targetSignal.Id, targetSignal.Index, targetSignal.Strike, targetSignal.OptionType,
                    message.MessageId, cancellableOrders.Count(o => o.Status == OrderStatus.Cancelled));
                return;
            }

            if (parser.RequiresActivation && parser.IsActivationMessage(message.Text))
            {
                TradingSignal? targetSignal = null;
                if (message.ReplyToMessageId.HasValue)
                {
                    targetSignal = await context.TradingSignals
                        .FirstOrDefaultAsync(s => s.TelegramMessageId == message.ReplyToMessageId.Value && s.Status == SignalStatus.AwaitingActivation);
                }

                if (targetSignal is null && !string.IsNullOrWhiteSpace(message.SenderName))
                {
                    targetSignal = await context.TradingSignals
                        .Where(s => s.ChannelName == message.SenderName && s.Status == SignalStatus.AwaitingActivation)
                        .OrderByDescending(s => s.ReceivedTimestamp)
                        .FirstOrDefaultAsync();
                }

                targetSignal ??= await context.TradingSignals
                        .Where(s => s.Status == SignalStatus.AwaitingActivation)
                        .OrderByDescending(s => s.ReceivedTimestamp)
                        .FirstOrDefaultAsync();

                if (targetSignal is not null && targetSignal.Status == SignalStatus.AwaitingActivation)
                {
                    _logger.LogInformation("Signal {SignalId} ({Channel}) activated by message {MessageId}", targetSignal.Id, targetSignal.ChannelName, message.MessageId);

                    var tradingMode = (await settings.GetSettingAsync<string>("TradingMode") ?? "manual").ToLowerInvariant();
                    var parsedSignal = await parser.ParseAsync(targetSignal.OriginalMessage, targetSignal.TelegramMessageId, targetSignal.TelegramTimestamp);
                    if (parsedSignal is not null)
                    {
                        parsedSignal.SignalTime = targetSignal.TelegramTimestamp;
                        parsedSignal.ChannelName = targetSignal.ChannelName;

                        var crossingEnabled = await settings.GetSettingAsync<bool?>("EnableEntryPriceCrossingTrigger") ?? false;

                        if (tradingMode == "auto" || tradingMode == "automatic")
                        {
                            if (crossingEnabled && parsedSignal.Action == SignalAction.Buy)
                            {
                                targetSignal.Status = SignalStatus.AwaitingEntry;
                                _logger.LogInformation(
                                    "Activated Signal {SignalId} set to AwaitingEntry — will execute when CMP crosses entry price {Entry}",
                                    targetSignal.Id, targetSignal.EntryPrice);
                            }
                            else
                            {
                                var engine = scope.ServiceProvider.GetRequiredService<ITradingEngine>();
                                var ok = await engine.ExecuteSignalAsync(parsedSignal);
                                var fresh = await context.TradingSignals.FindAsync(targetSignal.Id);
                                if (fresh != null)
                                {
                                    targetSignal.Status = (ok || fresh.Status == SignalStatus.Executed) ? SignalStatus.Executed : SignalStatus.Failed;
                                }
                                else
                                {
                                    targetSignal.Status = ok ? SignalStatus.Executed : SignalStatus.Failed;
                                }
                            }
                        }
                        else
                        {
                            // In manual mode, set signal to Parsed so it is unlocked and ready for manual execution
                            targetSignal.Status = SignalStatus.Parsed;
                        }

                        await context.SaveChangesAsync();

                        await _hub.Clients.All.SendAsync(SignalStatusChangedEvent, new
                        {
                            targetSignal.Id,
                            Status = targetSignal.Status.ToString()
                        });
                    }
                }
                return;
            }

            var receivedAt = DateTime.UtcNow;
            var parsed = await parser.ParseAsync(messageText, message.MessageId, message.Timestamp);

            // Copy/forward message if a destination channel is configured
            var currentSettings = _manager.Snapshot();
            var destChannel = currentSettings.DestinationChannel?.Trim();
            if (!string.IsNullOrWhiteSpace(destChannel))
            {
                var isFromDestChannel = string.Equals(message.SenderName, destChannel, StringComparison.OrdinalIgnoreCase);
                if (!isFromDestChannel)
                {
                    var isToday = message.Timestamp.EnsureUtc().ToIst().Date == DateTimeExtensions.IstToday();
                    var isNewMessage = message.Timestamp >= _serviceStartTime;

                    var shouldForward = isToday && isNewMessage && (!currentSettings.ForwardOnlySignals || (parsed is not null && parsed.IsValid));
                    if (shouldForward)
                    {
                        _logger.LogInformation("Forwarding message {MessageId} from '{From}' to '{To}'...",
                            message.MessageId, message.SenderName, destChannel);

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _manager.Client.ForwardMessageAsync(
                                    fromChannelName: message.SenderName,
                                    messageId: (int)message.MessageId,
                                    toChannelName: destChannel,
                                    dropAuthor: currentSettings.ForwardAsCopy
                                );
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error forwarding message {MessageId} from '{From}' to '{To}'",
                                    message.MessageId, message.SenderName, destChannel);
                            }
                        });
                    }
                }
            }

            if (parsed is null || !parsed.IsValid)
            {
                _logger.LogDebug("Non-signal message ignored: {Preview}",
                    messageText.Length > 80 ? messageText[..80] + "…" : messageText);
                return;
            }

            // Duplicate / revision guard.
            // A channel frequently re-states the SAME call within seconds: an edit of the
            // original post (same message id, corrected levels) or a fresh re-post of the same
            // contract. Matching only on TelegramMessageId let the re-post through and produced
            // a second row for one call. Any un-executed signal for the same channel + contract
            // received inside the revision window is therefore treated as the same call and
            // updated in place.
            // Duplicate / revision guard.
            // A channel frequently re-states the SAME call within seconds: an edit of the
            // original post (same message id, corrected levels) or a fresh re-post of the same
            // contract. Matching only on TelegramMessageId let the re-post through and produced
            // a second row for one call. Any un-executed signal for the same channel + contract
            // received inside the revision window is therefore treated as the same call and
            // updated in place.
            //
            // The channel AND the full contract must match. Telegram message ids are only
            // unique per channel, and this channel posts several different contracts within
            // the revision window (e.g. NIFTY 23500CE at 09:23 then SENSEX 75600CE at 09:26).
            // Without those guards a later, unrelated post overwrote the entry/SL/targets of an
            // earlier signal while leaving its index/strike/option type untouched, producing a
            // row that showed one contract with another contract's levels.
            var revisionCutoff = receivedAt - SignalRevisionWindow;
            var existing = await context.TradingSignals
                .Where(s => s.ChannelName == message.SenderName &&
                            s.Index == parsed.Index &&
                            s.Strike == parsed.Strike &&
                            s.OptionType == parsed.OptionType &&
                            s.Action == parsed.Action &&
                            (s.TelegramMessageId == message.MessageId ||
                             s.ReceivedTimestamp >= revisionCutoff))
                .OrderByDescending(s => s.ReceivedTimestamp)
                .FirstOrDefaultAsync();

            if (existing is not null)
            {
                // Already acted upon (or deliberately dropped) — never rewrite history.
                if (existing.Status is SignalStatus.Executed or SignalStatus.Failed or SignalStatus.Ignored)
                {
                    _logger.LogDebug(
                        "Message {MessageId} from {Channel} matches signal {SignalId} which is already {Status}; ignored.",
                        message.MessageId, message.SenderName, existing.Id, existing.Status);
                    return;
                }

                existing.TelegramMessageId = message.MessageId;
                existing.OriginalMessage = messageText;
                existing.TelegramTimestamp = message.Timestamp.EnsureUtc();
                existing.ProcessedTimestamp = DateTime.UtcNow;
                existing.EntryPrice = parsed.EntryPrice;
                existing.StopLoss = parsed.StopLoss;
                existing.Targets = [.. parsed.Targets];
                existing.ExpiryDate = parsed.ExpiryDate;
                await context.SaveChangesAsync();

                _logger.LogInformation(
                    "Message {MessageId} from {Channel} revised existing signal {SignalId} ({Index} {Strike}{OptionType}): entry={Entry} sl={Sl} targets=[{Targets}].",
                    message.MessageId, message.SenderName, existing.Id, existing.Index, existing.Strike,
                    existing.OptionType, existing.EntryPrice, existing.StopLoss, string.Join("/", existing.Targets));

                await _hub.Clients.All.SendAsync(SignalStatusChangedEvent, new
                {
                    existing.Id,
                    Status = existing.Status.ToString()
                });
                return;
            }

            var signal = new TradingSignal
            {
                TelegramMessageId = message.MessageId,
                OriginalMessage = messageText,
                TelegramTimestamp = message.Timestamp.EnsureUtc(),
                ReceivedTimestamp = receivedAt,
                ProcessedTimestamp = DateTime.UtcNow,
                Action = parsed.Action,
                Index = parsed.Index,
                Strike = parsed.Strike,
                OptionType = parsed.OptionType,
                EntryPrice = parsed.EntryPrice,
                StopLoss = parsed.StopLoss,
                Targets = parsed.Targets,
                ExpiryDate = parsed.ExpiryDate,
                Status = parser.RequiresActivation ? SignalStatus.AwaitingActivation : SignalStatus.Parsed,
                SignalDelayMs = (decimal)(receivedAt - message.Timestamp.EnsureUtc()).TotalMilliseconds,
                ChannelName = message.SenderName
            };
            parsed.ChannelName = message.SenderName;

            // Resolve the broker contract up-front so the Signals grid shows the actual
            // tradingsymbol (e.g. CRUDEOIL16SEP268700PE) rather than the raw Telegram text.
            // Previously Symbol was only populated at execution time, so a signal parked in
            // AwaitingEntry/AwaitingActivation displayed nothing resolved and a mis-resolved
            // contract could not be spotted until after the order had been sent.
            try
            {
                var symbolBuilder = scope.ServiceProvider.GetRequiredService<SymbolBuilder>();
                var resolved = await symbolBuilder.ResolveAsync(
                    parsed,
                    parsed.ExpiryDate == default ? null : parsed.ExpiryDate);

                if (!string.IsNullOrWhiteSpace(resolved.Symbol))
                {
                    signal.Symbol = resolved.Symbol;
                    if (signal.ExpiryDate == default)
                        signal.ExpiryDate = resolved.Expiry;

                    _logger.LogInformation(
                        "Signal symbol resolved at parse time: {Index} {Strike} {OptionType} → {Symbol} (exchange={Exchange}, source={Source})",
                        parsed.Index, parsed.Strike, parsed.OptionType,
                        resolved.Symbol, resolved.Exchange, resolved.Source);
                }
            }
            catch (Exception ex)
            {
                // Never block ingestion on symbol resolution; the engine resolves again at
                // execution time and will overwrite Symbol with the authoritative value.
                _logger.LogWarning(ex,
                    "Could not resolve symbol at parse time for {Index} {Strike} {OptionType}; the grid will show the parsed values until execution.",
                    parsed.Index, parsed.Strike, parsed.OptionType);
            }

            context.TradingSignals.Add(signal);
            await context.SaveChangesAsync();

            _logger.LogInformation(
                "Signal parsed: {Action} {Index} {Strike}{OptionType} entry={Entry} sl={Sl} delay={Delay}ms channel={Channel}",
                signal.Action, signal.Index, signal.Strike, signal.OptionType,
                signal.EntryPrice, signal.StopLoss, (int)signal.SignalDelayMs, signal.ChannelName);

            await _hub.Clients.All.SendAsync("SignalReceived", new
            {
                signal.Id,
                signal.Action,
                signal.Index,
                signal.Strike,
                signal.OptionType,
                signal.EntryPrice,
                signal.StopLoss,
                signal.Targets,
                signal.SignalDelayMs,
                signal.OriginalMessage,
                signal.ReceivedTimestamp,
                signal.ChannelName,
                RequiresManualAction = signal.Status is SignalStatus.Parsed or SignalStatus.Pending
            });

            var mode = (await settings.GetSettingAsync<string>("TradingMode") ?? "manual").ToLowerInvariant();
            if (signal.Status == SignalStatus.AwaitingActivation)
            {
                _logger.LogInformation("Signal {SignalId} from {Channel} is awaiting activation; holding execution until the channel's activation message is received.", signal.Id, signal.ChannelName);
                return;
            }

            if (mode == "auto" || mode == "automatic")
            {
                // Entry Price Crossing Trigger: hold execution until CMP crosses entry price.
                // The CmpStreamingService monitors prices every 1s and will trigger execution
                // when CMP >= EntryPrice. The 10-minute staleness guard still applies.
                var crossingEnabled = await settings.GetSettingAsync<bool?>("EnableEntryPriceCrossingTrigger") ?? false;
                if (crossingEnabled && parsed.Action == SignalAction.Buy)
                {
                    signal.Status = SignalStatus.AwaitingEntry;
                    await context.SaveChangesAsync();
                    _logger.LogInformation(
                        "Signal {SignalId} set to AwaitingEntry — will execute when CMP crosses entry price {Entry}",
                        signal.Id, signal.EntryPrice);

                    await _hub.Clients.All.SendAsync(SignalStatusChangedEvent, new
                    {
                        signal.Id,
                        Status = signal.Status.ToString()
                    });
                    return;
                }

                var engine = scope.ServiceProvider.GetRequiredService<ITradingEngine>();
                var ok = await engine.ExecuteSignalAsync(parsed);
                var fresh = await context.TradingSignals.FindAsync(signal.Id);
                if (fresh != null)
                {
                    signal.Status = (ok || fresh.Status == SignalStatus.Executed) ? SignalStatus.Executed : SignalStatus.Failed;
                }
                else
                {
                    signal.Status = ok ? SignalStatus.Executed : SignalStatus.Failed;
                }
                await context.SaveChangesAsync();

                await _hub.Clients.All.SendAsync(SignalStatusChangedEvent, new
                {
                    signal.Id,
                    Status = signal.Status.ToString()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling incoming Telegram message {MessageId}", message.MessageId);
        }
    }

    /// <summary>
    /// Exit/profit-booking phrases that indicate a channel is instructing followers to close
    /// an existing trade (as opposed to posting a new entry). Applied to EVERY monitored
    /// channel, because exit wording varies from channel to channel.
    ///
    /// Safety: matching a keyword alone never closes anything. The message must ALSO name a
    /// contract that maps to a currently-open position from the same channel (see
    /// <see cref="BuildContractTag"/>). That contract requirement is what stops chatter like
    /// "we booked profit yesterday" from triggering a live exit.
    /// </summary>
    private static readonly string[] ExitKeywords =
    [
        "CMP EXIT ALL",
        "EXIT ALL",
        "BOOK UR PROFIT",
        "BOOK YOUR PROFIT",
        "BOOK FULL PROFIT",
        "BOOK PARTIAL PROFIT",
        "BOOK PROFIT",
        "PROFIT BOOK",
        "SQUARE OFF",
        "SQUAREOFF",
        "EXIT NOW",
        "EXIT THE TRADE",
        "EXIT POSITION",
        "CLOSE POSITION",
        "CLOSE THE TRADE"
    ];

    private async Task<bool> TryHandleIntermediateSquareOffAsync(TradingDbContext context, TelegramMessage message, IServiceProvider services)
    {
        var channel = NormalizeTag(message.SenderName);
        var text = message.Text ?? string.Empty;
        var normalizedText = NormalizeTag(text);

        var matchedKeyword = ExitKeywords.FirstOrDefault(k =>
            text.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (matchedKeyword is null)
            return false;

        var positions = await context.Positions
            .Include(p => p.Signal)
            .Where(p => p.ClosedAt == null)
            .ToListAsync();

        var matchingPositions = positions
            .Where(p => p.Signal is not null &&
                        NormalizeTag(p.Signal.ChannelName) == channel &&
                        normalizedText.Contains(BuildContractTag(p.Signal), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingPositions.Count == 0)
        {
            // Deliberate no-op: an exit instruction was recognised but could not be tied to a
            // specific open position from this channel. We do NOT exit everything, because a
            // vague message must never square off unrelated trades. Surfaced as a warning so
            // the trade can be closed manually from the Positions page if it was genuine.
            var openFromChannel = positions.Count(p =>
                p.Signal is not null && NormalizeTag(p.Signal.ChannelName) == channel);

            if (openFromChannel > 0)
            {
                _logger.LogWarning(
                    "Exit keyword '{Keyword}' seen from {Channel} (message {MessageId}) but no contract in the text " +
                    "matched any of the {OpenCount} open position(s) from that channel. No action taken - review manually. Text: {Text}",
                    matchedKeyword, message.SenderName, message.MessageId, openFromChannel, text);
            }
            else
            {
                _logger.LogDebug(
                    "Exit keyword '{Keyword}' seen from {Channel} but there are no open positions from that channel.",
                    matchedKeyword, message.SenderName);
            }

            return true;
        }

        _logger.LogInformation(
            "Intermediate exit '{Keyword}' from {Channel} matched {Count} open position(s); squaring off.",
            matchedKeyword, message.SenderName, matchingPositions.Count);

        var engine = services.GetRequiredService<ITradingEngine>();
        foreach (var position in matchingPositions)
            await engine.SquareOffPositionAsync(position.Id);

        return true;
    }

    /// <summary>
    /// Applies a follow-up message that only quotes Target / stop-loss levels for a call
    /// posted moments earlier, e.g. the "Vip Group" channel posts
    /// "SENSEX 74400 PE ABOVE 345" and then follows with "TGT 370/400/450++" and "SL 300".
    /// The original signal (and any still-open position created from it) is updated with
    /// the quoted levels.
    ///
    /// Two shapes are supported:
    ///  - the follow-up <b>tags (replies to)</b> the original call — resolved by message id;
    ///  - the follow-up is just the next message in the channel (no reply link, or a reply to
    ///    some other chatter) — resolved by taking the most recent signal from the same
    ///    channel inside <see cref="FollowUpWindow"/>.
    ///
    /// A single channel may use both shapes interchangeably ("Vip Group" does), and the
    /// targets and stop-loss may arrive as two separate messages; each one is applied to the
    /// same parent call independently.
    ///
    /// Safety: only messages that name no contract of their own (no CE/PE/CALL/PUT) are
    /// treated as updates, so a fresh signal is still parsed normally. The untagged path is
    /// additionally time-boxed so an unrelated "SL hit" style message posted much later can
    /// never rewrite an old call's levels.
    /// </summary>
    private async Task<bool> TryHandleFollowUpUpdateAsync(TradingDbContext context, TelegramMessage message)
    {
        var text = message.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || ContractHintRegex().IsMatch(text))
            return false;

        var targetMatch = FollowUpTargetRegex().Match(text);
        var slMatch = FollowUpStopLossRegex().Match(text);
        if (!targetMatch.Success && !slMatch.Success)
            return false;

        var targets = targetMatch.Success ? ParseLevels(targetMatch.Groups[1].Value) : [];
        decimal? stopLoss = slMatch.Success &&
            decimal.TryParse(slMatch.Groups[1].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var sl) && sl > 0
            ? sl
            : null;

        if (targets.Count == 0 && stopLoss is null)
            return false;

        // Prefer the explicit reply link when the tagged message is a known call; otherwise
        // fall back to the most recent call from the same channel. The fallback also covers
        // replies that tag an intermediate message ("Good move possible") rather than the
        // signal itself, which the "Vip Group" channel does interchangeably.
        TradingSignal? signal = null;
        var resolvedByTag = false;

        if (message.ReplyToMessageId.HasValue)
        {
            signal = await context.TradingSignals
                .FirstOrDefaultAsync(s => s.TelegramMessageId == message.ReplyToMessageId.Value);
            resolvedByTag = signal is not null;
        }

        signal ??= await FindRecentSignalForFollowUpAsync(context, message);

        if (signal is null)
            return false;

        if (targets.Count > 0) signal.Targets = [.. targets];
        if (stopLoss is not null) signal.StopLoss = stopLoss.Value;

        var openPositions = await context.Positions
            .Where(p => p.SignalId == signal.Id && p.ClosedAt == null)
            .ToListAsync();

        foreach (var position in openPositions)
        {
            if (targets.Count > 0) position.Targets = [.. targets];
            if (stopLoss is not null) position.StopLoss = stopLoss.Value;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Follow-up message {MessageId} from {Channel} ({Link}) updated signal {SignalId} (targets: [{Targets}], SL: {StopLoss}) and {PositionCount} open position(s).",
            message.MessageId, message.SenderName,
            message.ReplyToMessageId.HasValue && resolvedByTag ? "tagged" : "same-channel window", signal.Id,
            string.Join("/", targets), stopLoss?.ToString() ?? "unchanged", openPositions.Count);

        await _hub.Clients.All.SendAsync(SignalStatusChangedEvent, new
        {
            signal.Id,
            Status = signal.Status.ToString()
        });

        return true;
    }

    /// <summary>
    /// Resolves the call an untagged Target/SL-only message belongs to: the most recently
    /// received signal from the same channel that is still inside <see cref="FollowUpWindow"/>
    /// and has not already been closed out. Returns null when no such call exists, in which
    /// case the message falls through to the normal parsing pipeline.
    /// </summary>
    private static async Task<TradingSignal?> FindRecentSignalForFollowUpAsync(
        TradingDbContext context, TelegramMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.SenderName))
            return null;

        var cutoff = DateTime.UtcNow - FollowUpWindow;

        return await context.TradingSignals
            .Where(s => s.ChannelName == message.SenderName &&
                        s.ReceivedTimestamp >= cutoff &&
                        s.Status != SignalStatus.Ignored &&
                        s.Status != SignalStatus.Failed)
            .OrderByDescending(s => s.ReceivedTimestamp)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Splits a quoted level list ("320/350", "320 350 400+") into positive decimals.
    /// </summary>
    private static List<decimal> ParseLevels(string raw) =>
        [.. raw.Split(['/', ' ', '+', ',', '\t'], StringSplitOptions.RemoveEmptyEntries)
              .Select(p => decimal.TryParse(p.Trim(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m)
              .Where(v => v > 0)];

    [GeneratedRegex(@"\b(CE|PE|CALL|PUT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContractHintRegex();

    [GeneratedRegex(@"(?:TARGETS?|TGT)[^\dA-Z]*(\d[\d\s/+.]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FollowUpTargetRegex();

    [GeneratedRegex(@"(?:SL|STOPLOSS)[^\dA-Z]*?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FollowUpStopLossRegex();

    private static string BuildContractTag(TradingSignal signal)
    {
        var strike = signal.Strike.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var optionType = signal.OptionType == OptionType.Ce ? "CE" : "PE";
        return NormalizeTag($"{signal.Index}{strike}{optionType}");
    }

    private static string NormalizeTag(string? value) =>
        new([.. (value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)]);

    private static async Task SafeDelay(TimeSpan delay, CancellationToken token)
        => await Task.Delay(delay, token);

    private static bool IsIgnoreMessage(string text)
        => !string.IsNullOrWhiteSpace(text) && 
           IgnoreMessageRegex().IsMatch(text);

    [GeneratedRegex(@"\bIGNORE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IgnoreMessageRegex();

    private static async Task<TradingSignal?> FindIgnoreTargetSignalAsync(
        TradingDbContext context,
        ISignalParser parser,
        TelegramMessage message)
    {
        // 1) Exact reply target (strongest signal linkage)
        if (message.ReplyToMessageId.HasValue)
        {
            var byReply = await context.TradingSignals
                .Where(s => s.TelegramMessageId == message.ReplyToMessageId.Value &&
                            s.Status != SignalStatus.Executed &&
                            s.Status != SignalStatus.Failed &&
                            s.Status != SignalStatus.Ignored)
                .OrderByDescending(s => s.ReceivedTimestamp)
                .FirstOrDefaultAsync();
            if (byReply is not null)
                return byReply;
        }

        // 2) Try to parse contract details from the IGNORE-tagged message text
        var parsed = await parser.ParseAsync(message.Text, message.MessageId, message.Timestamp);
        if (parsed is not null && parsed.IsValid)
        {
            var byContract = await context.TradingSignals
                .Where(s => s.Index == parsed.Index &&
                            s.Strike == parsed.Strike &&
                            s.OptionType == parsed.OptionType &&
                            s.Status != SignalStatus.Executed &&
                            s.Status != SignalStatus.Failed &&
                            s.Status != SignalStatus.Ignored &&
                            (string.IsNullOrWhiteSpace(message.SenderName) || s.ChannelName == message.SenderName))
                .OrderByDescending(s => s.ReceivedTimestamp)
                .FirstOrDefaultAsync();
            if (byContract is not null)
                return byContract;
        }

        // 3) Fallback: latest active signal in the same channel
        return await context.TradingSignals
            .Where(s => s.Status != SignalStatus.Executed &&
                        s.Status != SignalStatus.Failed &&
                        s.Status != SignalStatus.Ignored &&
                        (string.IsNullOrWhiteSpace(message.SenderName) || s.ChannelName == message.SenderName))
            .OrderByDescending(s => s.ReceivedTimestamp)
            .FirstOrDefaultAsync();
    }
}
