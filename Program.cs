using Serilog;
using NexusApp.Components;
using NexusApp.Data;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using NexusApp.BackgroundServices;
using NexusApp.Brokers.AngelOne;
using NexusApp.Interfaces;
using NexusApp.Notifications;
using NexusApp.Parser;
using NexusApp.Services;
using NexusApp.Telegram;
using NexusApp.TradingEngine;
using TE = NexusApp.TradingEngine.TradingEngine;

var builder = WebApplication.CreateBuilder(args);

// Serilog: console + daily rolling files
builder.Host.UseSerilog((context, configuration) =>
    configuration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            path: "Logs/trading-app-.txt",
            rollingInterval: RollingInterval.Day,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
            retainedFileCountLimit: 30));

// Blazor Server + SignalR + MudBlazor
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR()
    .AddJsonProtocol(opts =>
    {
        // Preserve property names as declared (PascalCase) so Blazor clients
        // can use case-sensitive JsonElement.TryGetProperty("SignalId", ...).
        opts.PayloadSerializerOptions.PropertyNamingPolicy = null;
    });
builder.Services.AddMudServices();

// Persistence
builder.Services.AddDbContext<TradingDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=trading.db"));
builder.Services.AddDbContextFactory<TradingDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=trading.db"),
    lifetime: ServiceLifetime.Scoped);

// Domain services
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<NexusApp.Services.DataCleanupService>();
builder.Services.AddScoped<ITradingAccountService, TradingAccountService>();
builder.Services.AddScoped<TradingSignalService>();
builder.Services.AddScoped<ISignalParser, SignalParser>();
builder.Services.AddScoped<RiskManager>();
builder.Services.AddScoped<PaperTradingEngine>();
builder.Services.AddScoped<SymbolBuilder>();
builder.Services.AddScoped<ITradingEngine, TE>();
builder.Services.AddScoped<INotificationService, NotificationService>();

// Angel One broker + HTTP client
builder.Services.AddHttpClient("AngelOne", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "NexusApp/1.0");
});
builder.Services.AddSingleton<AngelOneApiClient>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var apiUrl = builder.Configuration["AngelOne:ApiUrl"] ?? "https://apiconnect.angelone.in";
    return new AngelOneApiClient(
        httpFactory.CreateClient("AngelOne"),
        sp.GetRequiredService<ILogger<AngelOneApiClient>>(),
        apiUrl);
});
builder.Services.AddSingleton<AngelInstrumentMaster>();
builder.Services.AddSingleton<AngelOneWebSocketClient>();
builder.Services.AddSingleton<IBroker>(sp => new AngelOneBroker(
    sp.GetRequiredService<AngelOneApiClient>(),
    sp.GetRequiredService<ILogger<AngelOneBroker>>(),
    sp.GetRequiredService<AngelInstrumentMaster>()));

// Telegram + hosted services (singletons where required)
builder.Services.AddSingleton<TelegramClientWrapper>();
builder.Services.AddSingleton<TelegramManager>();
builder.Services.AddHostedService<TelegramListenerService>();
builder.Services.AddHostedService<BrokerAutoConnectService>();
builder.Services.AddHostedService<HealthMonitorService>();
builder.Services.AddHostedService<CmpStreamingService>();
builder.Services.AddHostedService<OrderSyncService>();
builder.Services.AddHostedService<DailyMaintenanceService>();

var app = builder.Build();

// Ensure database exists and seed defaults
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    context.Database.EnsureCreated();
    await DbSeeder.SeedAsync(context);
    Log.Information("Database initialized successfully");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHub<TradingHub>("/tradingHub");

Log.Information("Trading Application started");

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// SignalR hub broadcasting live signal / order / health updates to Blazor clients.
/// </summary>
public sealed class TradingHub : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly ILogger<TradingHub> _logger;
    public TradingHub(ILogger<TradingHub> logger) => _logger = logger;

    public override Task OnConnectedAsync()
    {
        _logger.LogDebug("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

internal static class DbSeeder
{
    // All default settings — used both for fresh installs and for adding missing keys to existing DBs.
    private static readonly (string Key, string Value, NexusApp.Models.SettingType Type, string Description)[] DefaultSettings =
    {
        ("TradingMode",     "manual", NexusApp.Models.SettingType.String,  "manual | auto"),
        ("PaperTrading",    "true",   NexusApp.Models.SettingType.Boolean, "Route orders through paper engine"),
        ("DefaultQuantity", "1",      NexusApp.Models.SettingType.Integer, "Default lots per trade"),
        ("StopLossBuffer",  "0",      NexusApp.Models.SettingType.Decimal, "Buffer added to signal stop-loss"),
        ("Lots.NIFTY",      "0",      NexusApp.Models.SettingType.Integer, "NIFTY lot override (0 = use DefaultQuantity)"),
        ("Lots.BANKNIFTY",  "0",      NexusApp.Models.SettingType.Integer, "BANKNIFTY lot override"),
        ("Lots.FINNIFTY",   "0",      NexusApp.Models.SettingType.Integer, "FINNIFTY lot override"),
        ("Lots.MIDCPNIFTY", "0",      NexusApp.Models.SettingType.Integer, "MIDCPNIFTY lot override"),
        ("Lots.SENSEX",     "0",      NexusApp.Models.SettingType.Integer, "SENSEX lot override"),
        ("Lots.BANKEX",     "0",      NexusApp.Models.SettingType.Integer, "BANKEX lot override"),
        ("DailyMaxLoss",    "1000",   NexusApp.Models.SettingType.Decimal, "Max loss per day in ₹"),
        ("DailyMaxProfit",  "5000",   NexusApp.Models.SettingType.Decimal, "Max profit target per day in ₹"),
        ("MaxOpenPositions","5",      NexusApp.Models.SettingType.Integer, "Max simultaneous open positions"),
        ("MaxTradesPerDay", "10",     NexusApp.Models.SettingType.Integer, "Max trades per day"),
        ("AllowAfterMarketHours", "false", NexusApp.Models.SettingType.Boolean, "Allow order placement outside market hours"),
        ("Watchlist.HiddenSignalIds", "", NexusApp.Models.SettingType.String, "Soft-hidden watchlist signal IDs"),
        ("KillSwitch",      "false",  NexusApp.Models.SettingType.Boolean, "Emergency kill switch"),
    };

    public static async Task SeedAsync(TradingDbContext context)
    {
        // Seed default trading account on fresh install
        if (!await context.TradingAccounts.AnyAsync())
        {
            context.TradingAccounts.Add(new NexusApp.Models.TradingAccount
            {
                Name             = "Paper Trading Account",
                BrokerType       = "AngelOne",
                ClientId         = "PAPER",
                IsEnabled        = true,
                IsDefault        = true,
                DailyMaxLoss     = 1000,
                DailyMaxProfit   = 5000,
                MaxOpenPositions = 5,
                MaxTradesPerDay  = 10,
                DefaultQuantity  = 1,
                CreatedAt        = DateTime.UtcNow,
                UpdatedAt        = DateTime.UtcNow
            });
        }

        // Upsert all default settings — adds missing keys to existing DBs without overwriting user changes
        var existingKeys = await context.ApplicationSettings
            .Select(s => s.Key)
            .ToHashSetAsync();

        foreach (var (key, value, type, desc) in DefaultSettings)
        {
            if (!existingKeys.Contains(key))
                context.ApplicationSettings.Add(new NexusApp.Models.ApplicationSetting(key, value, type, desc));
        }

        await context.SaveChangesAsync();
    }
}
