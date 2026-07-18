using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusApp.Infrastructure;
using NexusApp.Interfaces;

namespace NexusApp.Telegram;

/// <summary>
/// User-facing settings for the Telegram integration. Sensitive fields (api hash,
/// phone number) are stored encrypted via <see cref="CredentialProtector"/>.
/// </summary>
public sealed class TelegramSettings
{
    public bool Enabled { get; set; }
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string SessionPath { get; set; } = "telegram.session";
    /// <summary>Channels (title or @username) the listener should subscribe to.</summary>
    public List<string> Channels { get; set; } = new();
    public string DestinationChannel { get; set; } = string.Empty;
    public bool ForwardOnlySignals { get; set; } = true;
    public bool ForwardAsCopy { get; set; } = true;
}

/// <summary>
/// Singleton orchestrator that persists Telegram settings/channels, drives the
/// interactive login flow from the UI, and exposes state changes so the
/// background listener and UI stay in sync.
/// </summary>
public sealed class TelegramManager
{
    private const string KeyEnabled = "Telegram.Enabled";
    private const string KeyApiId = "Telegram.ApiId";
    private const string KeyApiHash = "Telegram.ApiHash";
    private const string KeyPhone = "Telegram.PhoneNumber";
    private const string KeySession = "Telegram.SessionPath";
    private const string KeyChannels = "Telegram.Channels";
    private const string KeyDestinationChannel = "Telegram.DestinationChannel";
    private const string KeyForwardOnlySignals = "Telegram.ForwardOnlySignals";
    private const string KeyForwardAsCopy = "Telegram.ForwardAsCopy";

    private readonly TelegramClientWrapper _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramManager> _logger;

    private TelegramSettings _current = new();
    private Task? _connectTask;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public TelegramManager(
        TelegramClientWrapper client,
        IServiceScopeFactory scopeFactory,
        ILogger<TelegramManager> logger)
    {
        _client = client;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public TelegramClientWrapper Client => _client;
    public TelegramConnectionState State => _client.State;
    public bool IsConnected => _client.IsConnected;

    /// <summary>Fires when the connection state changes (proxied from the wrapper).</summary>
    public event Action<TelegramConnectionState>? StateChanged
    {
        add => _client.StateChanged += value;
        remove => _client.StateChanged -= value;
    }

    /// <summary>Loads persisted settings from SQLite (decrypting sensitive fields).</summary>
    public async Task<TelegramSettings> LoadAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var enabled = await settings.GetSettingAsync<string>(KeyEnabled);
        var apiId = await settings.GetSettingAsync<int?>(KeyApiId);
        var apiHash = await settings.GetSettingAsync<string>(KeyApiHash);
        var phone = await settings.GetSettingAsync<string>(KeyPhone);
        var session = await settings.GetSettingAsync<string>(KeySession);
        var channels = await settings.GetSettingAsync<string>(KeyChannels);
        var destChannel = await settings.GetSettingAsync<string>(KeyDestinationChannel);
        var onlySignals = await settings.GetSettingAsync<string>(KeyForwardOnlySignals);
        var asCopy = await settings.GetSettingAsync<string>(KeyForwardAsCopy);

        _current = new TelegramSettings
        {
            Enabled = bool.TryParse(enabled, out var e) && e,
            ApiId = apiId ?? 0,
            ApiHash = string.IsNullOrEmpty(apiHash) ? string.Empty : CredentialProtector.Unprotect(apiHash),
            PhoneNumber = string.IsNullOrEmpty(phone) ? string.Empty : CredentialProtector.Unprotect(phone),
            SessionPath = string.IsNullOrWhiteSpace(session) ? "telegram.session" : session,
            Channels = ParseChannels(channels),
            DestinationChannel = destChannel ?? string.Empty,
            ForwardOnlySignals = !bool.TryParse(onlySignals, out var os) || os,
            ForwardAsCopy = !bool.TryParse(asCopy, out var ac) || ac
        };

        return _current;
    }

    /// <summary>Persists settings (encrypting sensitive fields) and updates the in-memory snapshot.</summary>
    public async Task SaveAsync(TelegramSettings updated)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        await settings.SetSettingAsync(KeyEnabled, updated.Enabled.ToString().ToLowerInvariant());
        await settings.SetSettingAsync(KeyApiId, updated.ApiId);
        await settings.SetSettingAsync(KeyApiHash, CredentialProtector.Protect(updated.ApiHash ?? string.Empty));
        await settings.SetSettingAsync(KeyPhone, CredentialProtector.Protect(updated.PhoneNumber ?? string.Empty));
        await settings.SetSettingAsync(KeySession, string.IsNullOrWhiteSpace(updated.SessionPath) ? "telegram.session" : updated.SessionPath);
        await settings.SetSettingAsync(KeyChannels, string.Join('\n', updated.Channels.Select(c => c.Trim()).Where(c => c.Length > 0)));
        await settings.SetSettingAsync(KeyDestinationChannel, (updated.DestinationChannel ?? string.Empty).Trim());
        await settings.SetSettingAsync(KeyForwardOnlySignals, updated.ForwardOnlySignals.ToString().ToLowerInvariant());
        await settings.SetSettingAsync(KeyForwardAsCopy, updated.ForwardAsCopy.ToString().ToLowerInvariant());

        _current = new TelegramSettings
        {
            Enabled = updated.Enabled,
            ApiId = updated.ApiId,
            ApiHash = updated.ApiHash ?? string.Empty,
            PhoneNumber = updated.PhoneNumber ?? string.Empty,
            SessionPath = string.IsNullOrWhiteSpace(updated.SessionPath) ? "telegram.session" : updated.SessionPath,
            Channels = updated.Channels.Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
            DestinationChannel = (updated.DestinationChannel ?? string.Empty).Trim(),
            ForwardOnlySignals = updated.ForwardOnlySignals,
            ForwardAsCopy = updated.ForwardAsCopy
        };
        _logger.LogInformation("Telegram settings saved ({ChannelCount} channels, enabled={Enabled})",
            _current.Channels.Count, _current.Enabled);
    }

    /// <summary>Returns the last-loaded settings snapshot without re-reading the DB.</summary>
    public TelegramSettings Snapshot() => _current;

    /// <summary>
    /// Starts a background connect using the current settings. Returns quickly;
    /// the caller should observe <see cref="State"/> and provide code/password
    /// via <see cref="SubmitCode"/>/<see cref="SubmitPassword"/> as needed.
    /// </summary>
    public async Task<bool> ConnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_client.IsConnected)
                return true;

            if (_connectTask is { IsCompleted: false })
                return false; // already connecting

            if (_current.ApiId <= 0 || string.IsNullOrWhiteSpace(_current.ApiHash) || string.IsNullOrWhiteSpace(_current.PhoneNumber))
            {
                _logger.LogWarning("Telegram credentials incomplete; cannot connect");
                return false;
            }

            var apiId = _current.ApiId;
            var apiHash = _current.ApiHash;
            var phone = _current.PhoneNumber;
            var session = _current.SessionPath;

            _connectTask = Task.Run(async () =>
            {
                try { await _client.InitializeAsync(apiId, apiHash, phone, session); }
                catch (Exception ex) { _logger.LogError(ex, "Telegram connect failed"); }
            });
            return true;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public Task DisconnectAsync() => _client.DisconnectAsync();

    public void SubmitCode(string code) => _client.SetVerificationCode(code);
    public void SubmitPassword(string password) => _client.SetTwoFactorPassword(password);

    /// <summary>Returns the broadcast channels the connected user has joined.</summary>
    public IReadOnlyList<AvailableChannel> ListAvailableChannels() => _client.ListAvailableChannels();

    private static List<string> ParseChannels(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return raw
            .Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
