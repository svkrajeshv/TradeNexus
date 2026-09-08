using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TL;
using WTelegram;

namespace NexusApp.Telegram;

/// <summary>
/// Connection state of the underlying Telegram MTProto client.
/// </summary>
public enum TelegramConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    AwaitingCode = 2,
    AwaitingPassword = 3,
    Connected = 4,
    Failed = 5
}

/// <summary>
/// Wrapper around WTelegramClient for reading messages from user-joined channels.
/// Provides an event-driven stream of new messages, best-effort polling fallback,
/// and an interactive login flow (verification code / 2FA password) suitable for a UI.
/// </summary>
public sealed class TelegramClientWrapper : IAsyncDisposable
{
    private readonly ILogger<TelegramClientWrapper> _logger;

    private Client? _client;
    private User? _me;
    private Dictionary<long, ChatBase>? _chats;
    private readonly ConcurrentDictionary<string, InputPeer> _channelPeerCache = new(StringComparer.OrdinalIgnoreCase);

    private int _apiId;
    private string _apiHash = string.Empty;
    private string _phoneNumber = string.Empty;
    private string _sessionPath = "telegram.session";
    private string? _verificationCode;
    private string? _twoFactorPassword;

    private readonly SemaphoreSlim _codeGate = new(0, 1);
    private readonly SemaphoreSlim _passwordGate = new(0, 1);
    private static readonly TimeSpan InteractiveWaitTimeout = TimeSpan.FromMinutes(10);

    private TelegramConnectionState _state = TelegramConnectionState.Disconnected;
    public TelegramConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    public event Action<TelegramConnectionState>? StateChanged;
    public event Func<TelegramMessage, Task>? MessageReceived;

    public bool IsConnected => State == TelegramConnectionState.Connected && _client is not null && _me is not null;
    public string? MeUsername => _me?.username ?? _me?.first_name;
    public long MeId => _me?.id ?? 0;
    public int ChatCount => _chats?.Count ?? 0;

    public TelegramClientWrapper(ILogger<TelegramClientWrapper> logger)
    {
        _logger = logger;
    }

    public void SetVerificationCode(string code)
    {
        _verificationCode = code;
        if (_codeGate.CurrentCount == 0)
        {
            try { _codeGate.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public void SetTwoFactorPassword(string password)
    {
        _twoFactorPassword = password;
        if (_passwordGate.CurrentCount == 0)
        {
            try { _passwordGate.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>
    /// Initializes and logs in the Telegram client. When Telegram requires a
    /// verification code or 2FA password, State transitions to
    /// AwaitingCode/AwaitingPassword and this method blocks internally until
    /// <see cref="SetVerificationCode"/> or <see cref="SetTwoFactorPassword"/> is called.
    /// </summary>
    public async Task<bool> InitializeAsync(int apiId, string apiHash, string phoneNumber, string? sessionPath = null)
    {
        try
        {
            _apiId = apiId;
            _apiHash = apiHash;
            _phoneNumber = phoneNumber;

            var configuredSessionPath = !string.IsNullOrWhiteSpace(sessionPath) ? sessionPath! : _sessionPath;
            _sessionPath = ResolveSessionPath(configuredSessionPath);

            if (_client is not null)
            {
                _client.Dispose();
                _client = null;
            }

            State = TelegramConnectionState.Connecting;
            _logger.LogInformation("Initializing Telegram client for {Phone} using session file {SessionPath}", MaskPhone(phoneNumber), _sessionPath);

            _client = CreateClientWithSessionFallback();
            _client.OnUpdates += OnUpdatesAsync;

            _me = await _client.LoginUserIfNeeded();

            // Telegram intermittently answers this call with "500 RPC_CALL_FAIL" - a
            // server-side hiccup, not an auth problem. The login itself is valid, so keep
            // the session and start with an empty dialog cache instead of tearing
            // everything down; RefreshDialogsAsync can populate it later.
            try
            {
                var dialogs = await _client.Messages_GetAllDialogs();
                _chats = dialogs.chats;
            }
            catch (Exception dialogEx)
            {
                _chats = new Dictionary<long, ChatBase>();
                _logger.LogWarning(dialogEx, "Telegram returned an error while loading the dialog list; continuing with an empty channel list");
            }

            State = TelegramConnectionState.Connected;
            _logger.LogInformation(
                "Telegram client connected as {User} ({Id}), {ChatCount} chats/channels available",
                MeUsername, MeId, ChatCount);

            return true;
        }
        catch (Exception ex)
        {
            _client?.Dispose();
            _client = null;
            _me = null;
            _chats = null;
            _channelPeerCache.Clear();

            State = TelegramConnectionState.Failed;
            _logger.LogError(ex, "Failed to initialize Telegram client");
            return false;
        }
    }

    public async Task<List<TelegramMessage>> PollChannelAsync(string channelName, int limit = 20)
    {
        var results = new List<TelegramMessage>();
        if (_client is null || _chats is null) return results;

        try
        {
            var peer = ResolveChannelPeer(channelName);
            if (peer is null)
            {
                _logger.LogWarning("Telegram channel not found: {Channel}", channelName);
                return results;
            }

            var history = await _client.Messages_GetHistory(peer, limit: limit);
            foreach (var m in history.Messages)
            {
                if (m is Message msg && !string.IsNullOrWhiteSpace(msg.message))
                {
                    results.Add(new TelegramMessage
                    {
                        MessageId = msg.id,
                        Text = msg.message,
                        Timestamp = msg.date,
                        SenderName = channelName,
                        ReplyToMessageId = msg.reply_to is MessageReplyHeader header ? (long?)header.reply_to_msg_id : null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling Telegram channel {Channel}", channelName);
        }

        return results;
    }

    /// <summary>
    /// Returns the broadcast channels the connected user has joined, sorted by title.
    /// </summary>
    public IReadOnlyList<AvailableChannel> ListAvailableChannels()
    {
        if (_chats is null) return Array.Empty<AvailableChannel>();
        var list = new List<AvailableChannel>();
        foreach (var kv in _chats)
        {
            if (kv.Value is Channel ch)
            {
                list.Add(new AvailableChannel
                {
                    Id = ch.id,
                    Title = ch.title ?? string.Empty,
                    Username = ch.username ?? string.Empty
                });
            }
        }
        return list.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Re-fetches the dialog list from Telegram and returns the refreshed channel list.
    /// Used by the UI when the initial fetch failed (e.g. "500 RPC_CALL_FAIL").
    /// </summary>
    public async Task<IReadOnlyList<AvailableChannel>> RefreshAvailableChannelsAsync()
    {
        if (_client is null || _me is null) return ListAvailableChannels();

        try
        {
            var dialogs = await _client.Messages_GetAllDialogs();
            _chats = dialogs.chats;
            _logger.LogInformation("Refreshed Telegram dialog list, {ChatCount} chats/channels available", ChatCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh the Telegram dialog list");
        }

        return ListAvailableChannels();
    }

    public Task<bool> DisconnectAsync()
    {
        try
        {
            _client?.Dispose();
            _client = null;
            _me = null;
            _chats = null;
            _channelPeerCache.Clear();
            _verificationCode = null;
            _twoFactorPassword = null;
            State = TelegramConnectionState.Disconnected;
            _logger.LogInformation("Telegram client disconnected");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting Telegram client");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Forwards a message from a source channel to a destination channel.
    /// If dropAuthor is true, it copies the message without "Forwarded from..." header.
    /// </summary>
    public async Task<bool> ForwardMessageAsync(string fromChannelName, int messageId, string toChannelName, bool dropAuthor = true)
    {
        if (_client is null)
        {
            _logger.LogWarning("Cannot forward message: Telegram client is not initialized.");
            return false;
        }

        try
        {
            var fromPeer = ResolveChannelPeer(fromChannelName);
            if (fromPeer is null)
            {
                _logger.LogWarning("Source channel peer not found: {Channel}", fromChannelName);
                return false;
            }

            var toPeer = ResolveChannelPeer(toChannelName);
            if (toPeer is null)
            {
                _logger.LogWarning("Destination channel peer not found: {Channel}", toChannelName);
                return false;
            }

            await _client.Messages_ForwardMessages(
                from_peer: fromPeer,
                id: new[] { messageId },
                random_id: new[] { Random.Shared.NextInt64() },
                to_peer: toPeer,
                drop_author: dropAuthor
            );

            _logger.LogInformation("Successfully forwarded message {MessageId} from '{From}' to '{To}' (dropAuthor={DropAuthor})",
                messageId, fromChannelName, toChannelName, dropAuthor);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward message {MessageId} from '{From}' to '{To}'", messageId, fromChannelName, toChannelName);
            return false;
        }
    }

    /// <summary>
    /// Sends a text message to a specific Telegram channel/chat, or to "Saved Messages" (Self) if targetChannel is null/empty.
    /// </summary>
    public async Task<bool> SendTextMessageAsync(string text, string? targetChannel = null)
    {
        if (_client is null || !IsConnected)
        {
            _logger.LogWarning("Cannot send Telegram message: Client is not initialized or not connected.");
            return false;
        }

        try
        {
            InputPeer? peer = null;
            if (!string.IsNullOrWhiteSpace(targetChannel))
            {
                peer = ResolveChannelPeer(targetChannel.Trim());
            }

            peer ??= new InputPeerSelf();

            await _client.SendMessageAsync(peer, text);
            _logger.LogInformation("Successfully sent Telegram notification to {Target}", string.IsNullOrWhiteSpace(targetChannel) ? "Saved Messages (Self)" : targetChannel);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram text message");
            return false;
        }
    }


    public ValueTask DisposeAsync()
    {
        _ = DisconnectAsync();
        return ValueTask.CompletedTask;
    }

    private InputPeer? ResolveChannelPeer(string channelName)
    {
        if (_chats is null) return null;
        if (_channelPeerCache.TryGetValue(channelName, out var cached)) return cached;

        foreach (var kv in _chats)
        {
            if (kv.Value is Channel ch &&
                (string.Equals(ch.title, channelName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(ch.username, channelName, StringComparison.OrdinalIgnoreCase)))
            {
                var peer = ch.ToInputPeer();
                _channelPeerCache[channelName] = peer;
                return peer;
            }
        }
        return null;
    }

    private async Task OnUpdatesAsync(UpdatesBase updates)
    {
        try
        {
            if (_chats is not null)
                updates.CollectUsersChats(new Dictionary<long, User>(), _chats);

            foreach (var update in updates.UpdateList)
            {
                Message? msg = update switch
                {
                    UpdateNewChannelMessage ucm => ucm.message as Message,
                    UpdateEditChannelMessage uec => uec.message as Message,
                    UpdateNewMessage un => un.message as Message,
                    UpdateEditMessage ue => ue.message as Message,
                    _ => null
                };

                if (msg is null || string.IsNullOrWhiteSpace(msg.message)) continue;

                var senderName = ResolveSenderName(msg);
                var replyToId = msg.reply_to is MessageReplyHeader header ? (long?)header.reply_to_msg_id : null;
                var tm = new TelegramMessage
                {
                    MessageId = msg.id,
                    Text = msg.message,
                    Timestamp = msg.date,
                    SenderName = senderName,
                    ReplyToMessageId = replyToId
                };

                if (MessageReceived is not null)
                    await MessageReceived.Invoke(tm);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Telegram update");
        }
    }

    private string ResolveSenderName(Message msg)
    {
        if (_chats is null) return string.Empty;
        var peerId = msg.peer_id switch
        {
            PeerChannel pc => pc.channel_id,
            PeerChat pchat => pchat.chat_id,
            _ => 0L
        };
        if (peerId != 0 && _chats.TryGetValue(peerId, out var chat))
        {
            return chat switch
            {
                Channel c => c.title,
                Chat ch => ch.title,
                _ => string.Empty
            };
        }
        return string.Empty;
    }

    private string? ConfigProvider(string what)
    {
        switch (what)
        {
            case "api_id": return _apiId.ToString();
            case "api_hash": return _apiHash;
            case "phone_number": return _phoneNumber;
            case "session_pathname": return _sessionPath;
            case "verification_code":
                if (string.IsNullOrEmpty(_verificationCode))
                {
                    State = TelegramConnectionState.AwaitingCode;
                    _logger.LogInformation("Telegram awaiting verification code");
                    if (!_codeGate.Wait(InteractiveWaitTimeout))
                        throw new TimeoutException("Timed out waiting for Telegram verification code");
                    State = TelegramConnectionState.Connecting;
                }
                var code = _verificationCode ?? string.Empty;
                _verificationCode = null;
                return code;
            case "password":
                if (string.IsNullOrEmpty(_twoFactorPassword))
                {
                    State = TelegramConnectionState.AwaitingPassword;
                    _logger.LogInformation("Telegram awaiting 2FA password");
                    if (!_passwordGate.Wait(InteractiveWaitTimeout))
                        throw new TimeoutException("Timed out waiting for Telegram 2FA password");
                    State = TelegramConnectionState.Connecting;
                }
                var pw = _twoFactorPassword ?? string.Empty;
                _twoFactorPassword = null;
                return pw;
            case "first_name": return "Trader";
            case "last_name": return "App";
            default: return null;
        }
    }

    private static string ResolveSessionPath(string configuredPath)
    {
        var normalized = string.IsNullOrWhiteSpace(configuredPath) ? "telegram.session" : configuredPath.Trim();

        if (Path.IsPathRooted(normalized))
        {
            EnsureSessionDirectoryExists(normalized);
            return normalized;
        }

        // On Azure App Service the %HOME% directory (D:\home) is durable, shared
        // network storage that survives app restarts, idle unloads and scaling.
        // Path.GetTempPath() (D:\local\Temp) is per-instance scratch storage that is
        // wiped on every recycle, which deletes the authorized Telegram session and
        // forces a fresh OTP login. Persist the session under %HOME% so the login
        // remains valid once the OTP is verified.
        var homeDir = Environment.GetEnvironmentVariable("HOME");
        var isAzureAppService = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"))
            && !string.IsNullOrWhiteSpace(homeDir);

        string resolvedPath;
        if (isAzureAppService)
        {
            // Durable, cross-instance persistent storage on Azure App Service.
            resolvedPath = Path.Combine(homeDir!, "data", "nexusapp", "telegram", normalized);
        }
        else
        {
            var appDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusApp", "telegram");
            resolvedPath = Path.Combine(appDataRoot, normalized);

            // If a session file exists in the current working directory / app base directory,
            // copy it to the persistent appDataRoot if appDataRoot doesn't have one yet.
            try
            {
                var localFile = Path.Combine(AppContext.BaseDirectory, normalized);
                if (File.Exists(localFile) && !File.Exists(resolvedPath))
                {
                    EnsureSessionDirectoryExists(resolvedPath);
                    File.Copy(localFile, resolvedPath, overwrite: false);
                }
            }
            catch { }
        }

        EnsureSessionDirectoryExists(resolvedPath);
        return resolvedPath;
    }

    private Client CreateClientWithSessionFallback()
    {
        const int maxRetries = 5;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return new Client(ConfigProvider);
            }
            catch (IOException ex) when (IsSessionFileLocked(ex))
            {
                if (attempt < maxRetries)
                {
                    _logger.LogWarning(ex,
                        "Telegram session file is locked: {SessionPath}. Retrying connection (attempt {Attempt}/{MaxRetries})...",
                        _sessionPath, attempt, maxRetries);
                    Thread.Sleep(1000);
                }
                else
                {
                    _logger.LogError(ex,
                        "Telegram session file is locked by another process: {SessionPath}. Could not acquire session file after {MaxRetries} retries.",
                        _sessionPath, maxRetries);
                    throw;
                }
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex,
                    "Telegram session file is invalid at {SessionPath}. Deleting it and retrying with a fresh session file.",
                    _sessionPath);

                TryDeleteSessionFile(_sessionPath);
                return new Client(ConfigProvider);
            }
        }

        return new Client(ConfigProvider);
    }

    private void TryDeleteSessionFile(string sessionPath)
    {
        try
        {
            if (File.Exists(sessionPath))
                File.Delete(sessionPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete invalid Telegram session file {SessionPath}", sessionPath);
        }
    }

    private static bool IsSessionFileLocked(IOException ex)
    {
        const int errorSharingViolation = 32;
        const int errorLockViolation = 33;
        var win32Code = ex.HResult & 0xFFFF;
        return win32Code == errorSharingViolation || win32Code == errorLockViolation;
    }

    private static void EnsureSessionDirectoryExists(string sessionFilePath)
    {
        var directory = Path.GetDirectoryName(sessionFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static string MaskPhone(string phone)
    {
        if (string.IsNullOrEmpty(phone) || phone.Length < 4) return "***";
        return string.Concat(phone.AsSpan(0, 3), "****", phone.AsSpan(phone.Length - 2));
    }
}

public sealed class TelegramMessage
{
    public long MessageId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public long? ReplyToMessageId { get; set; }
}

public sealed class AvailableChannel
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
}
