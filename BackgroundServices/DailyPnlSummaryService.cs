using NexusApp.Interfaces;

namespace NexusApp.BackgroundServices;

/// <summary>
/// Sends an end-of-day P&L summary to the Telegram order-notification channel
/// shortly after market close (15:45 IST). The summary lists each executed/closed
/// symbol with quantity and realised P&L, followed by the net total for the day.
///
/// Uses IST (UTC+5:30) since the exchange operates in that timezone regardless of
/// where the app is deployed.
/// </summary>
public sealed class DailyPnlSummaryService(
    ILogger<DailyPnlSummaryService> logger,
    IServiceProvider services) : BackgroundService
{
    private static readonly TimeSpan SummaryTimeIst = new(15, 45, 0);
    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330); // +05:30

    private readonly ILogger<DailyPnlSummaryService> _logger = logger;
    private readonly IServiceProvider _services = services;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Daily P&L summary service started (runs at {Time} IST every day)",
            SummaryTimeIst);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNextRun();
            _logger.LogInformation(
                "Next daily P&L summary in {Hours:0.0}h at {NextIst:yyyy-MM-dd HH:mm} IST",
                delay.TotalHours, IstNow().Add(delay));

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                using var scope = _services.CreateScope();
                var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
                await notifications.SendTelegramDailyPnlSummaryAsync();
                _logger.LogInformation("Daily P&L summary sent to Telegram");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily P&L summary run failed; will retry tomorrow");
            }
        }

        _logger.LogInformation("Daily P&L summary service stopped");
    }

    private static DateTime IstNow() => DateTime.UtcNow.Add(IstOffset);

    private static TimeSpan TimeUntilNextRun()
    {
        var istNow = IstNow();
        var todayRun = istNow.Date.Add(SummaryTimeIst);
        var next = istNow < todayRun ? todayRun : todayRun.AddDays(1);
        var delay = next - istNow;
        // Guard against clock jumps producing a zero/negative delay.
        return delay <= TimeSpan.Zero ? TimeSpan.FromHours(24) : delay;
    }
}
