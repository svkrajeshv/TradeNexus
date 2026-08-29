using Microsoft.Extensions.Logging;
using NexusApp.Interfaces;

namespace NexusApp.Services;

public class AuthService
{
    private const string KeyUsername = "Admin.Username";
    private const string KeyPassword = "Admin.Password";

    private readonly ISettingsService _settingsService;
    private readonly ILogger<AuthService> _logger;

    public AuthService(ISettingsService settingsService, ILogger<AuthService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<string> GetAdminUsernameAsync()
    {
        var stored = await _settingsService.GetSettingAsync<string>(KeyUsername);
        return string.IsNullOrWhiteSpace(stored) ? "admin" : stored;
    }

    public async Task<bool> ValidateCredentialsAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return false;

        var expectedUsername = await GetAdminUsernameAsync();
        var storedPassword = await _settingsService.GetSettingAsync<string>(KeyPassword);
        var expectedPassword = string.IsNullOrWhiteSpace(storedPassword) ? "admin123" : storedPassword;

        var isValid = string.Equals(username.Trim(), expectedUsername, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(password, expectedPassword);

        if (isValid)
        {
            _logger.LogInformation("Successful login for user '{Username}'", username);
        }
        else
        {
            _logger.LogWarning("Failed login attempt for user '{Username}'", username);
        }

        return isValid;
    }

    public async Task<bool> UpdateCredentialsAsync(string newUsername, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newUsername) || string.IsNullOrWhiteSpace(newPassword))
            return false;

        await _settingsService.SetSettingAsync(KeyUsername, newUsername.Trim());
        await _settingsService.SetSettingAsync(KeyPassword, newPassword);
        _logger.LogInformation("Admin credentials updated for user '{Username}'", newUsername);
        return true;
    }
}
