using NexusApp.Startup;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.ConfigureSerilog();
builder.Services.AddNexusAppServices(builder.Configuration);

var app = builder.Build();

await app.InitializeDatabaseAsync();
app.ConfigurePipeline();

Log.Information("Trading Application started");

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
