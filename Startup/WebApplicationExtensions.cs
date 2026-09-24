using NexusApp.Components;
using NexusApp.Data;
using NexusApp.Hubs;
using Serilog;

namespace NexusApp.Startup;

/// <summary>
/// Configures the host logging, HTTP request pipeline and startup database initialization.
/// </summary>
internal static class WebApplicationExtensions
{
    public static void ConfigureSerilog(this ConfigureHostBuilder host)
    {
        // Serilog: console + daily rolling files. Timestamps are emitted in IST via
        // the enricher so console, file and UI all agree with the exchange clock.
        const string outputTemplate =
            "[{IstTimestamp:l} IST] [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        host.UseSerilog((context, configuration) =>
            configuration
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.With<IstTimestampEnricher>()
                .WriteTo.Console(outputTemplate: outputTemplate)
                .WriteTo.File(
                    path: "Logs/trading-app-.txt",
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: outputTemplate,
                    retainedFileCountLimit: 5,
                    shared: true));
    }

    public static WebApplication ConfigurePipeline(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.MapHub<TradingHub>("/tradingHub");
        app.MapHealthChecks("/health");
        app.MapAuthEndpoints();

        return app;
    }

    // Apply EF Core migrations (with safe baseline for pre-migration databases) and seed defaults.
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        try
        {
            var context = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await DbSeeder.MigrateAsync(context);
            await DbSeeder.SeedAsync(context);
            Log.Information("Database initialized successfully");
        }
        catch (Exception ex)
        {
            // Never let a migration/seed hiccup take down the whole host (avoids 503 on startup).
            // The app can still boot; data-layer issues surface in logs and per-request handling.
            Log.Error(ex, "Database initialization failed during startup");
        }
    }
}
