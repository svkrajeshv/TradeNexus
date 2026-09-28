namespace NexusApp.Models;

/// <summary>
/// Represents an active risk limit alert when daily profit target or daily stop loss is reached.
/// </summary>
public sealed class RiskAlertInfo
{
    public required string Key { get; init; }
    public bool IsGlobal { get; init; }
    public int? AccountId { get; init; }
    public required string AccountName { get; init; }
    public bool IsProfitTarget { get; init; }
    public decimal CurrentPnL { get; init; }
    public decimal Limit { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
