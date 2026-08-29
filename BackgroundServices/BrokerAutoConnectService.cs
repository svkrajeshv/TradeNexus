using Microsoft.EntityFrameworkCore;
using NexusApp.Brokers.AliceBlue;
using NexusApp.Brokers.AngelOne;
using NexusApp.Data;
using NexusApp.Infrastructure;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Automatically connects the default enabled Angel One account on app startup
/// and keeps retrying in the background if disconnected.
/// </summary>
public sealed class BrokerAutoConnectService(
    ILogger<BrokerAutoConnectService> logger,
    IServiceProvider serviceProvider,
    IBroker broker,
    AngelOneApiClient apiClient,
    AngelInstrumentMaster instrumentMaster,
    AliceBlueApiClient aliceApiClient) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger<BrokerAutoConnectService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IBroker _broker = broker;
    private readonly AngelOneApiClient _apiClient = apiClient;
    private readonly AngelInstrumentMaster _instrumentMaster = instrumentMaster;
    private readonly AliceBlueApiClient _aliceApiClient = aliceApiClient;

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

                var accounts = await db.TradingAccounts
                    .AsNoTracking()
                    .Where(a => a.IsEnabled)
                    .OrderByDescending(a => a.IsDefault)
                    .ThenBy(a => a.Id)
                    .ToListAsync(stoppingToken);

                TradingAccount? account = null;
                BrokerCredentials creds = default;
                var snapshot = await settingsService.GetAllSettingsAsync();

                foreach (var acc in accounts)
                {
                    if (TryLoadCredentials(snapshot, acc, out var c))
                    {
                        account = acc;
                        creds = c;
                        break;
                    }
                }

                if (account is null)
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }

                // Dynamically resolve target broker from keyed services
                var broker = _serviceProvider.GetRequiredKeyedService<IBroker>(account.BrokerType);

                bool ok;
                if (account.BrokerType == "AngelOne")
                {
                    _apiClient.Configure(creds.ApiKey, creds.ApiUrl);
                    ok = await broker.AuthenticateAsync(account.ClientId, creds.Password, creds.TotpOrSecret);
                }
                else if (account.BrokerType == "AliceBlue")
                {
                    // AliceBlue authenticates with userId (ClientId) + apiKey; no password/TOTP.
                    _aliceApiClient.Configure(creds.ApiKey, creds.ApiUrl);
                    ok = await broker.AuthenticateAsync(account.ClientId, creds.Password, creds.ApiKey);
                }
                else
                {
                    ok = await broker.AuthenticateAsync(account.ClientId, creds.Password, creds.TotpOrSecret);
                }

                if (ok)
                {
                    _logger.LogInformation("Broker auto-connect successful for account {AccountId} ({BrokerType})", account.Id, account.BrokerType);
                    if (account.BrokerType == "AngelOne")
                    {
                        _ = _instrumentMaster.EnsureLoadedAsync(stoppingToken);
                    }
                }
                else
                    _logger.LogWarning("Broker auto-connect failed for account {AccountId} ({BrokerType})", account.Id, account.BrokerType);
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
            settings.TryGetValue($"{account.BrokerType}.Account.{account.Id}.{suffix}", out var value);
            return value ?? string.Empty;
        }

        var apiKey = Read("ApiKey");
        var apiUrl = Read("ApiUrl");

        if (account.BrokerType == "AliceBlue")
        {
            // AliceBlue only needs userId (ClientId) + apiKey.
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(account.ClientId))
                return false;

            creds = new BrokerCredentials(
                apiKey.Trim(),
                string.IsNullOrWhiteSpace(apiUrl) ? null : apiUrl.Trim(),
                string.Empty,
                string.Empty);
            return true;
        }

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

