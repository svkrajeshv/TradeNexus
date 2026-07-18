using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Infrastructure;
using NexusApp.Interfaces;
using NexusApp.Models;
using NexusApp.Brokers.AngelOne;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Automatically connects the default enabled Angel One account on app startup
/// and keeps retrying in the background if disconnected.
/// </summary>
public sealed class BrokerAutoConnectService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger<BrokerAutoConnectService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IBroker _broker;
    private readonly AngelOneApiClient _apiClient;
    private readonly AngelInstrumentMaster _instrumentMaster;

    public BrokerAutoConnectService(
        ILogger<BrokerAutoConnectService> logger,
        IServiceProvider serviceProvider,
        IBroker broker,
        AngelOneApiClient apiClient,
        AngelInstrumentMaster instrumentMaster)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _broker = broker;
        _apiClient = apiClient;
        _instrumentMaster = instrumentMaster;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Broker auto-connect service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_broker.IsConnected)
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                var account = await db.TradingAccounts
                    .AsNoTracking()
                    .Where(a => a.IsEnabled && a.BrokerType == "AngelOne")
                    .OrderByDescending(a => a.IsDefault)
                    .ThenBy(a => a.Id)
                    .FirstOrDefaultAsync(stoppingToken);

                if (account is null)
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }

                var snapshot = await settingsService.GetAllSettingsAsync();
                if (!TryLoadCredentials(snapshot, account, out var creds))
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }

                _apiClient.Configure(creds.ApiKey, creds.ApiUrl);
                var ok = await _broker.AuthenticateAsync(account.ClientId, creds.Password, creds.TotpOrSecret);

                if (ok)
                {
                    _logger.LogInformation("Broker auto-connect successful for account {AccountId}", account.Id);
                    _ = _instrumentMaster.EnsureLoadedAsync(stoppingToken);
                }
                else
                    _logger.LogWarning("Broker auto-connect failed for account {AccountId}", account.Id);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Broker auto-connect loop failed");
            }

            try { await Task.Delay(RetryDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Broker auto-connect service stopped");
    }

    private static bool TryLoadCredentials(
        Dictionary<string, string> settings,
        TradingAccount account,
        out BrokerCredentials creds)
    {
        creds = default;

        string Read(string suffix)
        {
            settings.TryGetValue($"AngelOne.Account.{account.Id}.{suffix}", out var value);
            return value ?? string.Empty;
        }

        var apiKey = Read("ApiKey");
        var apiUrl = Read("ApiUrl");
        var password = UnprotectIfPresent(Read("Password"));
        var totp = UnprotectIfPresent(Read("Totp"));

        if (string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(account.ClientId) ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(totp))
        {
            return false;
        }

        creds = new BrokerCredentials(
            apiKey.Trim(),
            string.IsNullOrWhiteSpace(apiUrl) ? null : apiUrl.Trim(),
            password,
            totp);
        return true;
    }

    private static string UnprotectIfPresent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        try { return CredentialProtector.Unprotect(value); }
        catch { return value; }
    }

    private readonly record struct BrokerCredentials(
        string ApiKey,
        string? ApiUrl,
        string Password,
        string TotpOrSecret);
}

