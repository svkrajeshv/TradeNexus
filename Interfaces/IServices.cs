namespace NexusApp.Interfaces;

/// <summary>
/// Interface for managing application settings
/// </summary>
public interface ISettingsService
{
    Task<T?> GetSettingAsync<T>(string key);
    Task SetSettingAsync<T>(string key, T value);
    Task<Dictionary<string, string>> GetAllSettingsAsync();
}

/// <summary>
/// Interface for trading engine operations
/// </summary>
public interface ITradingEngine
{
    Task<bool> ValidateSignalAsync(ParsedSignal signal);
    Task<bool> ExecuteSignalAsync(ParsedSignal signal, int? accountId = null);
    Task<bool> UpdatePositionsAsync();
    Task<bool> CheckRiskLimitsAsync(int accountId);
    Task<bool> SquareOffPositionAsync(int positionId);
}

/// <summary>
/// Interface for notification service
/// </summary>
public interface INotificationService
{
    Task SendSignalNotificationAsync(ParsedSignal signal);
    Task SendOrderNotificationAsync(string symbol, string action, string details);
    Task SendErrorNotificationAsync(string error);
    Task SendTelegramOrderPlacedAsync(string accountName, bool isPaper, string symbol, string action, decimal entryPrice, decimal quantity, string? orderId = null, string orderStatus = "ACCEPTED");
    Task SendTelegramOrderClosedAsync(string accountName, bool isPaper, string symbol, decimal entryPrice, decimal exitPrice, decimal realizedPnL, decimal quantity, string reason);
    Task SendTelegramCustomNotificationAsync(string message);
    Task SendTelegramDailyPnlSummaryAsync();
}

/// <summary>
/// Interface for trading account management
/// </summary>
public interface ITradingAccountService
{
    Task<List<TradingAccountDto>> GetAllAccountsAsync();
    Task<TradingAccountDto?> GetAccountAsync(int id);
    Task<int> CreateAccountAsync(TradingAccountDto account);
    Task<bool> UpdateAccountAsync(int id, TradingAccountDto account);
    Task<bool> DeleteAccountAsync(int id);
    Task<bool> SetDefaultAccountAsync(int id);
}

/// <summary>
/// DTO for trading account
/// </summary>
public class TradingAccountDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BrokerType { get; set; } = "AngelOne";
    public string ClientId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsDefault { get; set; }
    public decimal DailyMaxLoss { get; set; }
    public decimal DailyMaxProfit { get; set; }
    public int MaxOpenPositions { get; set; }
    public int MaxTradesPerDay { get; set; }
    public decimal DefaultQuantity { get; set; }
}
