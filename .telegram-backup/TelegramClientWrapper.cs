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
public sealed class TelegramClientWrapper(ILogger<TelegramClientWrapper> logger) : IAsyncDisposable
{
    private readonly ILogger<TelegramClientWrapper> _logger = logger;

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

    /// <summary>
    /// When every dialog query fails, further attempts are suppressed for this long. Without
    /// it each poll cycle would repeat the whole retry ladder against a server that is
    /// consistently answering 500, flooding the log and wasting round-trips.
    /// </summary>
    private static readonly TimeSpan DialogLoadCooldown = TimeSpan.FromMinutes(15);
    private DateTime _dialogLoadCooldownUntil = DateTime.MinValue;

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

    public void SetVerificationCode(string code)
    {
        _verificationCode = code;
        if (_codeGate.CurrentCount == 0)
        {
            // The gate was already released by a concurrent caller; the code is set either way.
            try { _codeGate.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
        }
    }

    public void SetTwoFactorPassword(string password)
    {
        _twoFactorPassword = password;
        if (_passwordGate.CurrentCount == 0)
        {
            // The gate was already released by a concurrent caller; the password is set either way.
            try { _passwordGate.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
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
            await DisposeClientAsync();

            // Access hashes are only valid for the client/session that issued them, so peers
            // cached against the previous client would fail with 400 CHANNEL_INVALID.
            _chats = null;
            _channelPeerCache.Clear();

            State = TelegramConnectionState.Connecting;
            _logger.LogInformation("Initializing Telegram client for {Phone} using session file {SessionPath}", MaskPhone(phoneNumber), _sessionPath);

            _client = CreateClientWithSessionFallback();
            _client.OnUpdates += OnUpdatesAsync;

            _me = await _client.LoginUserIfNeeded();

            // Telegram intermittently answers this call with "500 RPC_CALL_FAIL" (a
            // server-side hiccup, not a client/auth problem). Losing the whole session over
            // it would force a fresh login, so retry a few times and, if it still fails,
            // stay connected with an empty dialog cache - PollChannelAsync refreshes it
            // lazily on the next poll.
            _chats = await LoadDialogsWithRetryAsync();

            State = TelegramConnectionState.Connected;
            _logger.LogInformation(
                "Telegram client connected as {User} ({Id}), {ChatCount} chats/channels available",
                MeUsername, MeId, ChatCount);

            return true;
        }
        catch (Exception ex)
        {
            await DisposeClientAsync();
            _me = null;
            _chats = null;
            _channelPeerCache.Clear();

            State = TelegramConnectionState.Failed;
            _logger.LogError(ex, "Failed to initialize Telegram client");
            return false;
        }
    }

    /// <summary>
    /// Fetches the dialog list, retrying the transient server-side failures Telegram
    /// returns for this method (notably "500 RPC_CALL_FAIL" and other 5xx codes).
    ///
    /// Some accounts see <c>Messages_GetAllDialogs</c> fail with 500 persistently (a
    /// server-side rejection of the heavy dialog enumeration), so a bounded paged
    /// <c>Messages_GetDialogs</c> walk is attempted before giving up. Note that
    /// <c>Messages_GetAllChats</c> is not a useful fallback: WTelegramClient implements it on
    /// top of <c>Messages_GetAllDialogs</c>, so it fails identically.
    /// Returns an empty dictionary rather than throwing when everything fails, so a
    /// successful login is never discarded because of a temporary Telegram outage - the
    /// monitored channels are then resolved by username on demand.
    /// </summary>
    private async Task<Dictionary<long, ChatBase>> LoadDialogsWithRetryAsync()
    {
        // A single attempt: the paged variants below cover the retry need, and repeating the
        // heavy bulk call only burns the flood allowance (Telegram answers with FLOOD_WAIT
        // once too many dialog requests are made in quick succession).
        try
        {
            var dialogs = await _client!.Messages_GetAllDialogs();
            if (dialogs.chats.Count > 0)
                return dialogs.chats;
        }
        catch (RpcException ex) when (ex.Code >= 500)
        {
            _logger.LogWarning(
                "Telegram Messages_GetAllDialogs failed with {Code} {Message}; falling back to paged Messages_GetDialogs.",
                ex.Code, ex.Message);
        }
        catch (RpcException ex) when (IsFloodWait(ex))
        {
            ApplyFloodWaitCooldown(ex);
            return [];
        }

        // Page Messages_GetDialogs manually: the "all dialogs" variant walks every folder in
        // one shot, which is what Telegram rejects, while a bounded page sometimes succeeds.
        // Pinned dialogs and the archive folder are the usual culprits behind a persistent
        // 500. Every variant is tried and the results merged, because a variant that works
        // may cover only part of the account (e.g. only the archive folder responds).
        var merged = new Dictionary<long, ChatBase>();

        foreach (var (folderId, excludePinned, description) in DialogRequestVariants)
        {
            try
            {
                var chats = await LoadDialogsByPagingAsync(folderId, excludePinned);
                if (chats.Count > 0)
                {
                    foreach (var kv in chats)
                        merged[kv.Key] = kv.Value;

                    _logger.LogInformation(
                        "Paged Messages_GetDialogs ({Variant}) returned {ChatCount} chats/channels.",
                        description, chats.Count);
                }
                else
                {
                    _logger.LogWarning("Paged Messages_GetDialogs ({Variant}) returned no chats.", description);
                }
            }
            catch (RpcException ex) when (IsFloodWait(ex))
            {
                ApplyFloodWaitCooldown(ex);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Paged Messages_GetDialogs ({Variant}) failed.", description);
            }
        }

        if (merged.Count > 0)
        {
            _logger.LogInformation(
                "Telegram dialog cache built from paged Messages_GetDialogs; {ChatCount} chats/channels total.",
                merged.Count);
            return merged;
        }

        if (_dialogLoadCooldownUntil <= DateTime.UtcNow)
            _dialogLoadCooldownUntil = DateTime.UtcNow + DialogLoadCooldown;

        _logger.LogWarning(
            "All Telegram dialog queries failed; continuing with an empty dialog cache and retrying no earlier than {RetryAt:HH:mm:ss} UTC. " +
            "Monitored channels will be resolved by username on demand.",
            _dialogLoadCooldownUntil);

        return [];
    }

    private static bool IsFloodWait(RpcException ex) =>
        ex.Code == 420 || ex.Message.StartsWith("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Honours a FLOOD_WAIT_n response by suppressing dialog queries for the interval
    /// Telegram asked for (plus a small margin). Continuing to hammer the endpoint would only
    /// extend the ban.
    /// </summary>
    private void ApplyFloodWaitCooldown(RpcException ex)
    {
        var seconds = ex.X > 0 ? ex.X : (int)DialogLoadCooldown.TotalSeconds;
        var wait = TimeSpan.FromSeconds(seconds + 5);

        _dialogLoadCooldownUntil = DateTime.UtcNow + wait;
        _logger.LogWarning(
            "Telegram rate limited dialog queries ({Message}); suppressing further attempts until {RetryAt:HH:mm:ss} UTC.",
            ex.Message, _dialogLoadCooldownUntil);
    }

    /// <summary>
    /// Request shapes tried in order when the default dialog query fails. Excluding pinned
    /// dialogs and restricting to a single folder both reduce the work Telegram has to do,
    /// which is what makes the difference for accounts that answer the broad query with 500.
    /// </summary>
    private static readonly (int? FolderId, bool ExcludePinned, string Description)[] DialogRequestVariants =
    [
        (null, false, "all folders"),
        (0, false, "main folder"),
        (0, true, "main folder, no pinned"),
        (1, false, "archive folder")
    ];

    /// <summary>
    /// Walks the dialog list one bounded page at a time. Unlike
    /// <c>Messages_GetAllDialogs</c> this never asks Telegram to enumerate every folder in a
    /// single request, which is the shape of the call that returns 500 RPC_CALL_FAIL for
    /// some accounts.
    /// </summary>
    private async Task<Dictionary<long, ChatBase>> LoadDialogsByPagingAsync(int? folderId, bool excludePinned)
    {
        // Small pages keep the request well inside what Telegram's servers accept for the
        // accounts that reject the bulk variants with 500 RPC_CALL_FAIL.
        const int pageSize = 20;
        const int maxPages = 100;

        var collected = new Dictionary<long, ChatBase>();
        var offsetDate = default(DateTime);
        var offsetId = 0;
        InputPeer offsetPeer = new InputPeerSelf();

        for (var page = 0; page < maxPages; page++)
        {
            Messages_DialogsBase slice;
            try
            {
                slice = await _client!.Messages_GetDialogs(
                    offsetDate, offsetId, offsetPeer, pageSize, folder_id: folderId, exclude_pinned: excludePinned);
            }
            catch (RpcException ex) when (ex.Code >= 500)
            {
                // Keep whatever pages already succeeded instead of discarding the whole cache.
                _logger.LogWarning(
                    "Messages_GetDialogs page {Page} failed with {Code} {Message}; keeping {ChatCount} chats collected so far.",
                    page + 1, ex.Code, ex.Message, collected.Count);
                break;
            }
            if (slice is not Messages_Dialogs dialogs)
                break;

            foreach (var kv in dialogs.chats)
                collected[kv.Key] = kv.Value;

            if (slice is not Messages_DialogsSlice || dialogs.Dialogs.Length == 0)
                break;

            var lastDialog = dialogs.Dialogs[^1];
            var lastMessage = dialogs.Messages.FirstOrDefault(m => m.ID == lastDialog.TopMessage);
            if (lastMessage is null)
                break;

            offsetDate = lastMessage.Date;
            offsetId = lastDialog.TopMessage;
            offsetPeer = dialogs.UserOrChat(lastDialog.Peer).ToInputPeer();
        }

        return collected;
    }

    /// <summary>
    /// Reloads the dialog cache when it is empty (e.g. the initial fetch hit a Telegram
    /// 5xx). Safe to call on every poll: it is a no-op once chats are known.
    /// </summary>
    private async Task EnsureDialogsLoadedAsync()
    {
        if (_client is null || _chats is { Count: > 0 })
            return;

        if (DateTime.UtcNow < _dialogLoadCooldownUntil)
            return;

        var chats = await LoadDialogsWithRetryAsync();
        if (chats.Count > 0)
        {
            MergeDialogCache(chats);
            _dialogLoadCooldownUntil = DateTime.MinValue;
            _logger.LogInformation("Telegram dialog cache refreshed; {ChatCount} chats/channels available.", ChatCount);
        }
    }

    /// <summary>
    /// Merges a freshly loaded dialog set into the cache, keeping channels that were
    /// discovered by username/contact search. A partial dialog load (e.g. only the archive
    /// folder responds) must not evict peers the poller is already using.
    /// </summary>
    private void MergeDialogCache(Dictionary<long, ChatBase> chats)
    {
        if (_chats is null)
        {
            _chats = chats;
            return;
        }

        foreach (var kv in chats)
            _chats[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Forces a fresh dialog fetch, ignoring the failure cool-down, and returns the joined
    /// broadcast channels. Backs the "Refresh list" button: the user is explicitly asking to
    /// retry, so the throttle that protects the background poller does not apply.
    /// </summary>
    public async Task<IReadOnlyList<AvailableChannel>> RefreshAvailableChannelsAsync()
    {
        if (_client is null) return [];

        _dialogLoadCooldownUntil = DateTime.MinValue;

        var chats = await LoadDialogsWithRetryAsync();
        if (chats.Count > 0)
        {
            // Keep peers discovered by username/contact search that the dialog list omits.
            MergeDialogCache(chats);
            _dialogLoadCooldownUntil = DateTime.MinValue;
            _logger.LogInformation("Telegram dialog cache refreshed; {ChatCount} chats/channels available.", ChatCount);
        }

        return ListAvailableChannels();
    }

    public async Task<List<TelegramMessage>> PollChannelAsync(string channelName, int limit = 20)
    {
        var results = new List<TelegramMessage>();
        if (_client is null) return results;

        try
        {
            await EnsureDialogsLoadedAsync();

            var peer = await ResolveChannelPeerAsync(channelName);
            if (peer is null)
            {
                _logger.LogWarning("Telegram channel not found: {Channel}", channelName);
                return results;
            }

            var history = await GetHistoryWithPeerRefreshAsync(channelName, peer, limit);
            if (history is null)
                return results;

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
    /// Fetches history for a channel, recovering from the stale-peer errors Telegram returns
    /// when a cached access hash no longer belongs to the current session
    /// (400 CHANNEL_INVALID / PEER_ID_INVALID). The cached peer is evicted and resolved once
    /// more before the call is retried; returns null when the channel stays unresolvable.
    /// </summary>
    private async Task<Messages_MessagesBase?> GetHistoryWithPeerRefreshAsync(string channelName, InputPeer peer, int limit)
    {
        try
        {
            return await _client!.Messages_GetHistory(peer, limit: limit);
        }
        catch (RpcException ex) when (IsStalePeerError(ex))
        {
            _logger.LogWarning(
                "Telegram returned {Code} {Message} for channel {Channel}; the cached peer is stale, re-resolving.",
                ex.Code, ex.Message, channelName);

            InvalidateChannelPeer(channelName);

            var refreshed = await ResolveChannelPeerAsync(channelName);
            if (refreshed is null)
            {
                _logger.LogWarning(
                    "Could not re-resolve Telegram channel {Channel} after {Message}; skipping this poll.",
                    channelName, ex.Message);
                return null;
            }

            return await _client!.Messages_GetHistory(refreshed, limit: limit);
        }
    }

    private static bool IsStalePeerError(RpcException ex) =>
        ex.Message.Contains("CHANNEL_INVALID", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drops every cached lookup for a channel so the next resolve starts from scratch,
    /// including the dialog-cache entry that produced the stale access hash.
    /// </summary>
    private void InvalidateChannelPeer(string channelName)
    {
        _channelPeerCache.TryRemove(channelName, out _);

        if (_chats is null) return;

        var stale = _chats
            .Where(kv => kv.Value is Channel ch &&
                         (string.Equals(ch.title, channelName, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(ch.username, channelName, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in stale)
            _chats.Remove(id);
    }

    /// <summary>
    /// Returns the broadcast channels the connected user has joined, sorted by title.
    /// </summary>
    public IReadOnlyList<AvailableChannel> ListAvailableChannels()
    {
        if (_chats is null) return [];
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
        return [.. list.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<bool> DisconnectAsync()
    {
        try
        {
            await DisposeClientAsync();
            _me = null;
            _chats = null;
            _channelPeerCache.Clear();
            _verificationCode = null;
            _twoFactorPassword = null;
            State = TelegramConnectionState.Disconnected;
            _logger.LogInformation("Telegram client disconnected");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting Telegram client");
            return false;
        }
    }

    /// <summary>
    /// Releases the current client, preferring the asynchronous disposal path so the
    /// MTProto connection is torn down without blocking the calling thread.
    /// </summary>
    private async Task DisposeClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is null) return;

        if (client is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            await client.DisposeAsync();
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
            var fromPeer = await ResolveChannelPeerAsync(fromChannelName);
            if (fromPeer is null)
            {
                _logger.LogWarning("Source channel peer not found: {Channel}", fromChannelName);
                return false;
            }

            var toPeer = await ResolveChannelPeerAsync(toChannelName);
            if (toPeer is null)
            {
                _logger.LogWarning("Destination channel peer not found: {Channel}", toChannelName);
                return false;
            }

            await _client.Messages_ForwardMessages(
                from_peer: fromPeer,
                id: [messageId],
                random_id: [Random.Shared.NextInt64()],
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
                peer = await ResolveChannelPeerAsync(targetChannel.Trim());
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


    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    private InputPeer? ResolveChannelPeer(string channelName)
    {
        if (_channelPeerCache.TryGetValue(channelName, out var cached)) return cached;
        if (_chats is null) return null;

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

    /// <summary>
    /// Resolves a channel peer without relying on the dialog cache, which some accounts
    /// cannot populate because Telegram answers every dialog RPC with 500 RPC_CALL_FAIL.
    /// Order: cached peer, then a username lookup (public channels configured by @username),
    /// then a contact search by title (works for private channels the user has joined).
    /// </summary>
    private async Task<InputPeer?> ResolveChannelPeerAsync(string channelName)
    {
        var peer = ResolveChannelPeer(channelName);
        if (peer is not null || _client is null) return peer;

        var candidate = channelName.TrimStart('@').Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        // A username can never contain a space, so skip the RPC for display titles.
        if (!candidate.Contains(' '))
        {
            try
            {
                var resolved = await _client.Contacts_ResolveUsername(candidate);
                if (resolved.Chat is Channel channel)
                    return CacheResolvedChannel(channelName, channel, "username lookup");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Username lookup failed for Telegram channel {Channel}", channelName);
            }
        }

        // Contacts_Search matches joined chats/channels by title, so it covers private
        // channels and titles with spaces that Contacts_ResolveUsername cannot handle.
        try
        {
            var found = await _client.Contacts_Search(candidate, limit: 20);
            foreach (var chat in found.chats.Values)
            {
                if (chat is Channel channel &&
                    (string.Equals(channel.title, channelName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(channel.username, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return CacheResolvedChannel(channelName, channel, "contact search");
                }
            }

            _logger.LogDebug(
                "Contact search for Telegram channel {Channel} returned {Count} chats but no exact title match.",
                channelName, found.chats.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Contact search failed for Telegram channel {Channel}", channelName);
        }

        return null;
    }

    /// <summary>
    /// Caches a channel resolved outside the dialog list so subsequent lookups, update
    /// handling and <see cref="ListAvailableChannels"/> can all see it.
    /// </summary>
    private InputPeer CacheResolvedChannel(string channelName, Channel channel, string via)
    {
        var peer = channel.ToInputPeer();
        _channelPeerCache[channelName] = peer;

        _chats ??= [];
        _chats[channel.id] = channel;

        _logger.LogInformation(
            "Resolved Telegram channel {Channel} by {Via} (dialog cache unavailable).", channelName, via);
        return peer;
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Seeding the persistent location from the local copy is best-effort:
                // WTelegramClient recreates the session (with a new login) when it is missing.
                System.Diagnostics.Debug.WriteLine($"Could not seed Telegram session file: {ex.Message}");
            }
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
