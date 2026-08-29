using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NexusApp.Brokers.AngelOne;

/// <summary>
/// Angel One SmartAPI WebSocket 2.0 client (SmartStream).
///
/// Endpoint:      wss://smartapisocket.angelone.in/smart-stream
/// Headers:       Authorization, x-api-key, x-client-code, x-feed-token
/// Subscribe:     JSON { correlationID, action:1, params:{mode, tokenList:[{exchangeType, tokens[]}]} }
/// Ticks:         BINARY packets (LTP mode = 51 bytes; token/LTP encoded per Angel spec)
/// Heartbeat:     send "ping" text every 30s to keep the connection alive
///
/// Packet layout (LTP mode = 1):
///   byte 0       : Subscription Mode
///   byte 1       : Exchange Type
///   bytes 2..26  : Token (25 bytes, null-terminated ASCII)
///   bytes 27..34 : Sequence Number (int64 LE)
///   bytes 35..42 : Exchange Timestamp (int64 LE)
///   bytes 43..46 : LTP (int32 LE, price × 100)
/// </summary>
public sealed class AngelOneWebSocketClient : IAsyncDisposable
{
    private const string DefaultWsUrl = "wss://smartapisocket.angelone.in/smart-stream";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(10);

    private readonly AngelOneApiClient _apiClient;
    private readonly ILogger<AngelOneWebSocketClient> _logger;

    private readonly ConcurrentDictionary<string, decimal> _lastLtpByToken = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SubscribedToken> _subscribed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _loopCts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private DateTime _lastConnectAttempt = DateTime.MinValue;
    private long _tickCount;

    public AngelOneWebSocketClient(AngelOneApiClient apiClient, ILogger<AngelOneWebSocketClient> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public bool IsOpen => _socket is not null && _socket.State == WebSocketState.Open;

    /// <summary>Event raised for every parsed tick (token + LTP).</summary>
    public event Action<string, decimal>? OnTick;

    /// <summary>Returns the last known LTP for a token, or 0 if never received.</summary>
    public decimal GetLastLtp(string token) =>
        _lastLtpByToken.TryGetValue(token, out var v) ? v : 0m;

    /// <summary>
    /// Ensures the WS is connected and subscribes to the given tokens.
    /// Safe to call multiple times — will only send subscribe for new tokens.
    /// </summary>
    public async Task SubscribeAsync(IEnumerable<(string Token, string Exchange)> tokens, CancellationToken ct = default)
    {
        var newTokens = new List<SubscribedToken>();
        foreach (var (token, exchange) in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            var entry = new SubscribedToken(token, MapExchangeType(exchange));
            if (_subscribed.TryAdd(token, entry))
                newTokens.Add(entry);
        }

        if (newTokens.Count == 0 && IsOpen)
            return;

        await EnsureConnectedAsync(ct);
        if (!IsOpen)
            return;

        // Always resubscribe the full set on (re)connect so nothing is missed.
        var all = _subscribed.Values.ToList();
        await SendSubscribeAsync(all, ct);
    }

    /// <summary>
    /// Unsubscribes the given tokens from the live feed and drops their cached LTP.
    /// Safe to call for tokens that were never subscribed (they are simply ignored).
    /// </summary>
    public async Task UnsubscribeAsync(IEnumerable<(string Token, string Exchange)> tokens, CancellationToken ct = default)
    {
        var removed = new List<SubscribedToken>();
        foreach (var (token, exchange) in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (_subscribed.TryRemove(token, out var entry))
                removed.Add(entry);
            else
                removed.Add(new SubscribedToken(token, MapExchangeType(exchange)));
            _lastLtpByToken.TryRemove(token, out _);
        }

        if (removed.Count == 0 || !IsOpen)
            return;

        await SendUnsubscribeAsync(removed, ct);
    }

    /// <summary>
    /// Clears the entire subscription set and cached ticks. Used by the daily maintenance
    /// job so each trading day starts clean and does not re-subscribe expired contracts
    /// accumulated over a long-running process.
    /// </summary>
    public void ResetSubscriptions()
    {
        _subscribed.Clear();
        _lastLtpByToken.Clear();
        _logger.LogInformation("Cleared Angel WS subscription set and cached ticks for daily reset");
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsOpen)
            return;

        if (DateTime.UtcNow - _lastConnectAttempt < ReconnectBackoff)
            return;

        _lastConnectAttempt = DateTime.UtcNow;

        if (!_apiClient.IsAuthenticated ||
            string.IsNullOrWhiteSpace(_apiClient.JwtToken) ||
            string.IsNullOrWhiteSpace(_apiClient.FeedToken) ||
            string.IsNullOrWhiteSpace(_apiClient.ApiKey) ||
            string.IsNullOrWhiteSpace(_apiClient.ClientCode))
        {
            _logger.LogDebug("WebSocket connect skipped: broker not authenticated (need JWT + feedToken + apiKey + clientCode)");
            return;
        }

        await DisposeSocketAsync();

        try
        {
            _socket = new ClientWebSocket();
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {_apiClient.JwtToken}");
            _socket.Options.SetRequestHeader("x-api-key", _apiClient.ApiKey!);
            _socket.Options.SetRequestHeader("x-client-code", _apiClient.ClientCode!);
            _socket.Options.SetRequestHeader("x-feed-token", _apiClient.FeedToken!);

            await _socket.ConnectAsync(new Uri(DefaultWsUrl), ct);
            _logger.LogInformation("Connected to Angel One SmartStream at {Url}", DefaultWsUrl);

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_socket, _loopCts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_loopCts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Angel WebSocket connect failed; will retry after backoff");
            await DisposeSocketAsync();
        }
    }

    private async Task SendSubscribeAsync(List<SubscribedToken> tokens, CancellationToken ct)
    {
        if (_socket is null || _socket.State != WebSocketState.Open || tokens.Count == 0)
            return;

        // Group tokens by exchange type
        var groups = tokens
            .GroupBy(t => t.ExchangeType)
            .Select(g => new
            {
                exchangeType = g.Key,
                tokens = g.Select(x => x.Token).ToArray()
            })
            .ToArray();

        var payload = new
        {
            correlationID = Guid.NewGuid().ToString("N")[..12],
            action = 1,   // 1 = subscribe
            @params = new
            {
                mode = 1, // 1 = LTP mode (smallest packet)
                tokenList = groups
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            _logger.LogInformation(
                "Angel WS subscribed to {Count} token(s) across {Groups} exchange group(s)",
                tokens.Count, groups.Length);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendUnsubscribeAsync(List<SubscribedToken> tokens, CancellationToken ct)
    {
        if (_socket is null || _socket.State != WebSocketState.Open || tokens.Count == 0)
            return;

        var groups = tokens
            .GroupBy(t => t.ExchangeType)
            .Select(g => new
            {
                exchangeType = g.Key,
                tokens = g.Select(x => x.Token).ToArray()
            })
            .ToArray();

        var payload = new
        {
            correlationID = Guid.NewGuid().ToString("N")[..12],
            action = 0,   // 0 = unsubscribe
            @params = new
            {
                mode = 1, // must match the subscribe mode (LTP)
                tokenList = groups
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            _logger.LogInformation("Angel WS unsubscribed from {Count} token(s)", tokens.Count);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var pingBytes = Encoding.UTF8.GetBytes("ping");
        while (!ct.IsCancellationRequested && IsOpen)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct);
                if (!IsOpen) return;

                await _sendLock.WaitAsync(ct);
                try
                {
                    await _socket!.SendAsync(pingBytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                }
                finally { _sendLock.Release(); }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Heartbeat send failed");
                return;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        // Angel binary tick packet is 51 bytes for LTP mode; use larger buffer for safety
        var buffer = new byte[8 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Angel WS closed by remote: {Status} {Desc}", result.CloseStatus, result.CloseStatusDescription);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage && !ct.IsCancellationRequested);

                if (ms.Length == 0) continue;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    ParseBinaryTick(ms.ToArray());
                }
                else
                {
                    // Server may send text control frames (e.g. "pong")
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    if (!text.Equals("pong", StringComparison.OrdinalIgnoreCase))
                        _logger.LogDebug("Angel WS text: {Text}", text);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Angel WS receive loop terminated");
        }
    }

    private void ParseBinaryTick(byte[] data)
    {
        // Minimum LTP mode packet size is 51 bytes.
        if (data.Length < 47)
            return;

        try
        {
            var mode = data[0];
            // var exchangeType = data[1];
            var tokenBytes = new ReadOnlySpan<byte>(data, 2, 25);
            var nullIdx = tokenBytes.IndexOf((byte)0);
            var tokenLen = nullIdx >= 0 ? nullIdx : tokenBytes.Length;
            var token = Encoding.ASCII.GetString(tokenBytes[..tokenLen]).Trim();
            if (string.IsNullOrWhiteSpace(token))
                return;

            // LTP is int32 LE at offset 43 (in paise / 1e2 for equity; Angel keeps consistent × 100 scaling)
            var ltpRaw = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(data, 43, 4));
            var ltp = ltpRaw / 100m;

            if (ltp <= 0) return;

            _lastLtpByToken[token] = ltp;
            _tickCount++;
            if (_tickCount <= 5 || _tickCount % 100 == 0)
                _logger.LogInformation("Angel WS tick #{Count}: token={Token} LTP={Ltp}", _tickCount, token, ltp);
            OnTick?.Invoke(token, ltp);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Angel WS tick");
        }
    }

    /// <summary>
    /// Maps human exchange segment (NFO/BFO/NSE/BSE/CDS/MCX) to Angel exchangeType code.
    /// 1=NSE_CM, 2=NSE_FO, 3=BSE_CM, 4=BSE_FO, 5=MCX_FO, 7=NCX_FO, 13=CDE_FO
    /// </summary>
    private static int MapExchangeType(string exchange)
    {
        return (exchange ?? string.Empty).ToUpperInvariant() switch
        {
            "NSE"  => 1,
            "NFO"  => 2,
            "BSE"  => 3,
            "BFO"  => 4,
            "MCX"  => 5,
            "NCX"  => 7,
            "CDS"  => 13,
            _      => 2 // default to NFO for options
        };
    }

    private async Task DisposeSocketAsync()
    {
        try
        {
            _loopCts?.Cancel();
        }
        catch { }

        try
        {
            if (_socket is not null && (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived))
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }

        _socket?.Dispose();
        _socket = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSocketAsync();
        _sendLock.Dispose();
    }

    private readonly record struct SubscribedToken(string Token, int ExchangeType);
}
