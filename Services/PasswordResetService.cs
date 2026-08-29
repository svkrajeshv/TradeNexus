using System.Security.Cryptography;
using System.Text;
using NexusApp.Telegram;

namespace NexusApp.Services;

public sealed class PasswordResetService
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromMinutes(1);
    private const int MaxAttempts = 5;

    private readonly TelegramManager _telegramManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PasswordResetService> _logger;
    private readonly object _sync = new();

    private ResetRequest? _pendingRequest;
    private DateTime _lastRequestedAt = DateTime.MinValue;

    public PasswordResetService(
        TelegramManager telegramManager,
        IServiceScopeFactory scopeFactory,
        ILogger<PasswordResetService> logger)
    {
        _telegramManager = telegramManager;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> RequestResetAsync(string phoneNumber)
    {
        var settings = await _telegramManager.LoadAsync();
        if (!IsConfiguredPhone(phoneNumber, settings.PhoneNumber) || !_telegramManager.IsConnected)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (now - _lastRequestedAt < RequestCooldown)
            {
                return false;
            }
        }

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var delivered = await _telegramManager.Client.SendTextMessageAsync(
            $"NexusApp password reset code: {code}. It expires in 10 minutes. Do not share this code.");

        if (!delivered)
        {
            _logger.LogWarning("Password reset code could not be delivered through Telegram.");
            return false;
        }

        lock (_sync)
        {
            _lastRequestedAt = now;
            _pendingRequest = new ResetRequest(Hash(code), now.Add(CodeLifetime));
        }

        return true;
    }

    public async Task<bool> ResetPasswordAsync(string code, string newPassword)
    {
        var normalizedCode = NormalizeCode(code);
        if (normalizedCode.Length != 6 || newPassword.Length < 8)
        {
            _logger.LogWarning("Password reset confirmation rejected due to invalid form values (code length: {CodeLength}).", normalizedCode.Length);
            return false;
        }

        lock (_sync)
        {
            if (_pendingRequest is null || _pendingRequest.ExpiresAt <= DateTime.UtcNow)
            {
                _pendingRequest = null;
                _logger.LogWarning("Password reset confirmation rejected because no active code exists or the code expired.");
                return false;
            }

            if (_pendingRequest.Attempts >= MaxAttempts)
            {
                _pendingRequest = null;
                return false;
            }

            _pendingRequest.Attempts++;
            if (!CryptographicOperations.FixedTimeEquals(_pendingRequest.CodeHash, Hash(normalizedCode)))
            {
                _logger.LogWarning("Password reset confirmation rejected because the submitted code did not match.");
                return false;
            }

            _pendingRequest = null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var authService = scope.ServiceProvider.GetRequiredService<AuthService>();
        var username = await authService.GetAdminUsernameAsync();
        var updated = await authService.UpdateCredentialsAsync(username, newPassword);

        if (updated)
        {
            _logger.LogInformation("Application password was reset after Telegram verification.");
        }

        return updated;
    }

    private static bool IsConfiguredPhone(string phoneNumber, string configuredPhone)
    {
        configuredPhone = NormalizePhone(configuredPhone);
        var submittedPhone = NormalizePhone(phoneNumber);
        return configuredPhone.Length > 0 &&
               submittedPhone.Length > 0 &&
               string.Equals(configuredPhone, submittedPhone, StringComparison.Ordinal);
    }

    private static string NormalizePhone(string? value)
        => new(value?.Where(char.IsDigit).ToArray() ?? Array.Empty<char>());

    private static string NormalizeCode(string? value)
        => new(value?.Where(char.IsDigit).ToArray() ?? Array.Empty<char>());

    private static byte[] Hash(string value)
        => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private sealed class ResetRequest(byte[] codeHash, DateTime expiresAt)
    {
        public byte[] CodeHash { get; } = codeHash;
        public DateTime ExpiresAt { get; } = expiresAt;
        public int Attempts { get; set; }
    }
}
