using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Interfaces;

namespace NexusApp.Brokers;

/// <summary>
/// A proxy broker that routes calls to the appropriate IBroker implementation (AngelOne or AliceBlue)
/// depending on the active/default trading account stored in the database.
/// </summary>
public sealed class RoutingBroker(IServiceProvider serviceProvider) : IBroker
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private IBroker GetActiveBroker()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var activeAccount = context.TradingAccounts
                .AsNoTracking()
                .Where(a => a.IsEnabled)
                .OrderByDescending(a => a.IsDefault)
                .ThenBy(a => a.Id)
                .FirstOrDefault();

            if (activeAccount != null)
            {
                var broker = _serviceProvider.GetKeyedService<IBroker>(activeAccount.BrokerType);
                if (broker != null) return broker;
            }
        }
        catch
        {
            // fallback
        }
        return _serviceProvider.GetRequiredKeyedService<IBroker>("AngelOne");
    }

    public string BrokerName => GetActiveBroker().BrokerName;
    
    public bool IsConnected => GetActiveBroker().IsConnected;

    public Task<bool> AuthenticateAsync(string clientId, string password, string? twoFactorCode = null)
    {
        return GetActiveBroker().AuthenticateAsync(clientId, password, twoFactorCode);
    }

    public Task<bool> LogoutAsync() => GetActiveBroker().LogoutAsync();
    
    public Task<bool> RefreshTokenAsync() => GetActiveBroker().RefreshTokenAsync();
    
    public Task<BrokerAccountInfo?> GetAccountInfoAsync() => GetActiveBroker().GetAccountInfoAsync();
    
    public Task<BrokerInstrument?> SearchInstrumentAsync(string symbol) => GetActiveBroker().SearchInstrumentAsync(symbol);
    
    public Task<decimal> GetLiveQuoteAsync(string symbol) => GetActiveBroker().GetLiveQuoteAsync(symbol);

    public Task ReleaseSymbolFeedAsync(string symbol) => GetActiveBroker().ReleaseSymbolFeedAsync(symbol);
    
    public Task<BrokerOrderResponse> PlaceOrderAsync(BrokerOrderRequest request) => GetActiveBroker().PlaceOrderAsync(request);
    
    public Task<bool> ModifyOrderAsync(string orderId, BrokerOrderRequest request) => GetActiveBroker().ModifyOrderAsync(orderId, request);
    
    public Task<bool> CancelOrderAsync(string orderId) => GetActiveBroker().CancelOrderAsync(orderId);

    public Task<bool> ExitBracketOrderAsync(string parentOrderId, string symbol) =>
        GetActiveBroker().ExitBracketOrderAsync(parentOrderId, symbol);
    
    public Task<List<BrokerOrder>> GetOrderBookAsync() => GetActiveBroker().GetOrderBookAsync();
    
    public Task<List<BrokerPosition>> GetPositionBookAsync() => GetActiveBroker().GetPositionBookAsync();
    
    public Task<List<BrokerTrade>> GetTradeBookAsync() => GetActiveBroker().GetTradeBookAsync();
}
