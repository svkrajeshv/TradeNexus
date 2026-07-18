using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Models;
using NexusApp.Telegram;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Background service that connects to Telegram, listens for signals in real time,
/// persists them, executes them according to the configured trading mode and pushes
/// updates to connected UI clients via SignalR.
/// Configuration comes from the DB-persisted <see cref="TelegramManager"/> so that
/// changes made in the UI take effect on the next reconnect without an app restart.
/// </summary>
public sealed class TelegramListenerService : BackgroundService
{
    private readonly ILogger<TelegramListenerService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly TelegramManager _manager;
    private readonly IHubContext<TradingHub> _hub;
    private readonly ConcurrentDictionary<(string Channel, long MessageId), byte> _processedMessages = new();
    private readonly ConcurrentDictionary<string, int> _lastSeenMessageIds = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _serviceStartTime = DateTime.UtcNow;

    public TelegramListenerService(
        ILogger<TelegramListenerService> logger,
        IConfiguration config,
        IServiceProvider serviceProvider,
        TelegramManager manager,
        IHubContext<TradingHub> hub)
    {
        _logger = logger;
        _config = config;
        _serviceProvider = serviceProvider;
        _manager = manager;
        _hub = hub;
    }

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
                            
                            foreach (var msg in recent)
                                await HandleIncomingAsync(msg);
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

    private async Task HandleIncomingAsync(TelegramMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.SenderName))
        {
            _lastSeenMessageIds.AddOrUpdate(
                message.SenderName,
                (int)message.MessageId,
                (key, oldVal) => Math.Max(oldVal, (int)message.MessageId)
            );
        }

        if (!_processedMessages.TryAdd((message.SenderName, message.MessageId), 0))
            return;

        if (_processedMessages.Count > 2000)
        {
            foreach (var key in _processedMessages.Keys.OrderBy(k => k.MessageId).Take(500).ToList())
                _processedMessages.TryRemove(key, out _);
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var parser = scope.ServiceProvider.GetRequiredService<ISignalParser>();
            var context = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

            var receivedAt = DateTime.UtcNow;
            var parsed = await parser.ParseAsync(message.Text, message.MessageId, message.Timestamp);

            // Copy/forward message if a destination channel is configured
            var currentSettings = _manager.Snapshot();
            var destChannel = currentSettings.DestinationChannel?.Trim();
            if (!string.IsNullOrWhiteSpace(destChannel))
            {
                var isFromDestChannel = string.Equals(message.SenderName, destChannel, StringComparison.OrdinalIgnoreCase);
                if (!isFromDestChannel)
                {
                    var isToday = message.Timestamp.Date == DateTime.UtcNow.Date;
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
                    message.Text.Length > 80 ? message.Text[..80] + "…" : message.Text);
                return;
            }

            var duplicate = await context.TradingSignals
                .AsNoTracking()
                .AnyAsync(s => s.TelegramMessageId == message.MessageId);
            if (duplicate) return;

            var signal = new TradingSignal
            {
                TelegramMessageId = message.MessageId,
                OriginalMessage = message.Text,
                TelegramTimestamp = message.Timestamp,
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
                Status = SignalStatus.Parsed,
                SignalDelayMs = (decimal)(receivedAt - message.Timestamp).TotalMilliseconds
            };

            context.TradingSignals.Add(signal);
            await context.SaveChangesAsync();

            _logger.LogInformation(
                "Signal parsed: {Action} {Index} {Strike}{OptionType} entry={Entry} sl={Sl} delay={Delay}ms",
                signal.Action, signal.Index, signal.Strike, signal.OptionType,
                signal.EntryPrice, signal.StopLoss, (int)signal.SignalDelayMs);

            await _hub.Clients.All.SendAsync("SignalReceived", new
            {
                signal.Id,
                signal.Action,
                signal.Index,
                signal.Strike,
                signal.OptionType,
                signal.EntryPrice,
                signal.StopLoss,
                Targets = signal.Targets,
                signal.SignalDelayMs,
                signal.OriginalMessage,
                signal.ReceivedTimestamp
            });

            var mode = (await settings.GetSettingAsync<string>("TradingMode") ?? "manual").ToLowerInvariant();
            if (mode == "auto" || mode == "automatic")
            {
                var engine = scope.ServiceProvider.GetRequiredService<ITradingEngine>();
                var ok = await engine.ExecuteSignalAsync(parsed);
                signal.Status = ok ? SignalStatus.Executed : SignalStatus.Failed;
                await context.SaveChangesAsync();

                await _hub.Clients.All.SendAsync("SignalStatusChanged", new
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

    private static async Task SafeDelay(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token); } catch (OperationCanceledException) { }
    }
}
