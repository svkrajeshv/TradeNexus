using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using NexusApp.BackgroundServices;
using NexusApp.Brokers.AngelOne;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Notifications;
using NexusApp.Parser;
using NexusApp.Services;
using NexusApp.Telegram;
using NexusApp.TradingEngine;
using TE = NexusApp.TradingEngine.TradingEngine;

namespace NexusApp.Startup;

/// <summary>
/// Registers all application services with the DI container.
/// </summary>
internal static class ServiceCollectionExtensions
{
    private const string AngelOneClientName = "AngelOne";
    private const string AliceBlueClientName = "AliceBlue";

    public static IServiceCollection AddNexusAppServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPresentationServices();
        services.AddPersistence(configuration);
        services.AddAppAuthentication();
        services.AddDomainServices();
        services.AddBrokers(configuration);
        services.AddHostedServices();
        return services;
    }

    private static void AddPresentationServices(this IServiceCollection services)
    {
        // Blazor Server + SignalR + MudBlazor
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddSignalR()
            .AddJsonProtocol(opts =>
            {
                // Preserve property names as declared (PascalCase) so Blazor clients
                // can use case-sensitive JsonElement.TryGetProperty("SignalId", ...).
                opts.PayloadSerializerOptions.PropertyNamingPolicy = null;
            });
        services.AddMudServices();

        // Health checks (readiness probe incl. database connectivity)
        services.AddHealthChecks()
            .AddDbContextCheck<TradingDbContext>("database");
    }

    private static void AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=trading.db";
        var busyTimeoutInterceptor = new SqliteBusyTimeoutInterceptor();
        services.AddDbContext<TradingDbContext>(options =>
            options.UseSqlite(connectionString).AddInterceptors(busyTimeoutInterceptor));
        services.AddDbContextFactory<TradingDbContext>(
            options => options.UseSqlite(connectionString).AddInterceptors(busyTimeoutInterceptor),
            lifetime: ServiceLifetime.Scoped);
    }

    private static void AddAppAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.LogoutPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });
        services.AddCascadingAuthenticationState();
        services.AddHttpContextAccessor();
        services.AddScoped<AuthenticationStateProvider, CustomAuthStateProvider>();
        services.AddScoped<AuthService>();
        services.AddSingleton<PasswordResetService>();
    }

    private static void AddDomainServices(this IServiceCollection services)
    {
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<DataCleanupService>();
        services.AddScoped<TradeHistoryService>();
        services.AddScoped<ITradingAccountService, TradingAccountService>();
        services.AddScoped<TradingSignalService>();
        services.AddScoped<ISignalParser, SignalParser>();
        // Channel-aware signal parser strategies (highest priority wins).
        services.AddScoped<SignalParser>();
        services.AddScoped<IChannelSignalParser, SignalParser>();
        services.AddScoped<IChannelSignalParser, NexusApp.Parser.Channels.AbcBtstSignalParser>();
        services.AddScoped<IChannelSignalParser, NexusApp.Parser.Channels.IntradayNiftyParser>();
        services.AddScoped<IChannelSignalParser, NexusApp.Parser.Channels.TradeWithPihuParser>();
        services.AddScoped<IChannelSignalParser, NexusApp.Parser.Channels.TradeWithMohitAgrawalParser>();
        services.AddScoped<SignalParserResolver>();
        services.AddScoped<RiskManager>();
        services.AddScoped<PaperTradingEngine>();
        services.AddScoped<SymbolBuilder>();
        services.AddScoped<ITradingEngine, TE>();
        services.AddScoped<INotificationService, NotificationService>();
    }

    private static void AddBrokers(this IServiceCollection services, IConfiguration configuration)
    {
        // Angel One broker + HTTP client
        services.AddHttpClient(AngelOneClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "NexusApp/1.0");
        });
        services.AddSingleton<AngelOneApiClient>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var apiUrl = configuration[$"{AngelOneClientName}:ApiUrl"] ?? "https://apiconnect.angelone.in";
            return new AngelOneApiClient(
                httpFactory.CreateClient(AngelOneClientName),
                sp.GetRequiredService<ILogger<AngelOneApiClient>>(),
                apiUrl);
        });
        services.AddSingleton<AngelInstrumentMaster>();
        services.AddSingleton<AngelOneWebSocketClient>();
        services.AddSingleton<AngelOneBroker>(sp => new AngelOneBroker(
            sp.GetRequiredService<AngelOneApiClient>(),
            sp.GetRequiredService<ILogger<AngelOneBroker>>(),
            sp.GetRequiredService<AngelInstrumentMaster>(),
            sp.GetRequiredService<AngelOneWebSocketClient>()));

        services.AddKeyedSingleton<IBroker, AngelOneBroker>(AngelOneClientName, (sp, key) => sp.GetRequiredService<AngelOneBroker>());

        // AliceBlue broker + HTTP client (ANT v2 REST)
        services.AddHttpClient(AliceBlueClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "NexusApp/1.0");
        });
        services.AddSingleton<NexusApp.Brokers.AliceBlue.AliceBlueApiClient>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var apiUrl = configuration[$"{AliceBlueClientName}:ApiUrl"]
                ?? "https://ant.aliceblueonline.com/rest/AliceBlueAPIService";
            return new NexusApp.Brokers.AliceBlue.AliceBlueApiClient(
                httpFactory.CreateClient(AliceBlueClientName),
                sp.GetRequiredService<ILogger<NexusApp.Brokers.AliceBlue.AliceBlueApiClient>>(),
                apiUrl);
        });
        services.AddSingleton<NexusApp.Brokers.AliceBlue.AliceBlueContractMaster>();
        services.AddSingleton<NexusApp.Brokers.AliceBlue.AliceBlueBroker>(sp => new NexusApp.Brokers.AliceBlue.AliceBlueBroker(
            sp.GetRequiredService<NexusApp.Brokers.AliceBlue.AliceBlueApiClient>(),
            sp.GetRequiredService<ILogger<NexusApp.Brokers.AliceBlue.AliceBlueBroker>>(),
            sp.GetRequiredService<NexusApp.Brokers.AliceBlue.AliceBlueContractMaster>()));
        services.AddKeyedSingleton<IBroker, NexusApp.Brokers.AliceBlue.AliceBlueBroker>(
            AliceBlueClientName, (sp, key) => sp.GetRequiredService<NexusApp.Brokers.AliceBlue.AliceBlueBroker>());

        services.AddSingleton<IBroker, NexusApp.Brokers.RoutingBroker>();
    }

    private static void AddHostedServices(this IServiceCollection services)
    {
        // Telegram + hosted services (singletons where required)
        services.AddSingleton<TelegramClientWrapper>();
        services.AddSingleton<TelegramManager>();
        services.AddHostedService<TelegramListenerService>();
        services.AddHostedService<BrokerAutoConnectService>();
        services.AddHostedService<HealthMonitorService>();
        services.AddHostedService<CmpStreamingService>();
        services.AddHostedService<OrderSyncService>();
        services.AddHostedService<DailyMaintenanceService>();
        services.AddHostedService<DailyPnlSummaryService>();
        services.AddHostedService<AutoSquareOffService>();
        services.AddHostedService<LivePositionMonitorService>();
    }
}
