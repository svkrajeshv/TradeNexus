using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// Service for managing trading accounts
/// </summary>
public class TradingAccountService : ITradingAccountService
{
    private readonly TradingDbContext _context;
    private readonly ILogger<TradingAccountService> _logger;

    public TradingAccountService(TradingDbContext context, ILogger<TradingAccountService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<TradingAccountDto>> GetAllAccountsAsync()
    {
        try
        {
            var accounts = await _context.TradingAccounts
                .AsNoTracking()
                .ToListAsync();

            return accounts.Select(MapToDto).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all accounts");
            return new List<TradingAccountDto>();
        }
    }

    public async Task<TradingAccountDto?> GetAccountAsync(int id)
    {
        try
        {
            var account = await _context.TradingAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id);

            return account != null ? MapToDto(account) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving account: {Id}", id);
            return null;
        }
    }

    public async Task<int> CreateAccountAsync(TradingAccountDto accountDto)
    {
        try
        {
            var account = new TradingAccount
            {
                Name = accountDto.Name,
                BrokerType = accountDto.BrokerType,
                ClientId = accountDto.ClientId,
                IsEnabled = accountDto.IsEnabled,
                IsDefault = accountDto.IsDefault,
                DailyMaxLoss = accountDto.DailyMaxLoss,
                DailyMaxProfit = accountDto.DailyMaxProfit,
                MaxOpenPositions = accountDto.MaxOpenPositions,
                MaxTradesPerDay = accountDto.MaxTradesPerDay,
                DefaultQuantity = accountDto.DefaultQuantity,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.TradingAccounts.Add(account);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Account created: {Id} - {Name}", account.Id, account.Name);
            return account.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating account: {Name}", accountDto.Name);
            throw;
        }
    }

    public async Task<bool> UpdateAccountAsync(int id, TradingAccountDto accountDto)
    {
        try
        {
            var account = await _context.TradingAccounts.FindAsync(id);
            if (account == null)
            {
                _logger.LogWarning("Account not found: {Id}", id);
                return false;
            }

            account.Name = accountDto.Name;
            account.BrokerType = accountDto.BrokerType;
            account.ClientId = accountDto.ClientId;
            account.IsEnabled = accountDto.IsEnabled;
            account.IsDefault = accountDto.IsDefault;
            account.DailyMaxLoss = accountDto.DailyMaxLoss;
            account.DailyMaxProfit = accountDto.DailyMaxProfit;
            account.MaxOpenPositions = accountDto.MaxOpenPositions;
            account.MaxTradesPerDay = accountDto.MaxTradesPerDay;
            account.DefaultQuantity = accountDto.DefaultQuantity;
            account.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            _logger.LogInformation("Account updated: {Id}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating account: {Id}", id);
            return false;
        }
    }

    public async Task<bool> DeleteAccountAsync(int id)
    {
        try
        {
            var account = await _context.TradingAccounts.FindAsync(id);
            if (account == null)
            {
                _logger.LogWarning("Account not found: {Id}", id);
                return false;
            }

            _context.TradingAccounts.Remove(account);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Account deleted: {Id}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting account: {Id}", id);
            return false;
        }
    }

    private static TradingAccountDto MapToDto(TradingAccount account)
    {
        return new TradingAccountDto
        {
            Id = account.Id,
            Name = account.Name,
            BrokerType = account.BrokerType,
            ClientId = account.ClientId,
            IsEnabled = account.IsEnabled,
            IsDefault = account.IsDefault,
            DailyMaxLoss = account.DailyMaxLoss,
            DailyMaxProfit = account.DailyMaxProfit,
            MaxOpenPositions = account.MaxOpenPositions,
            MaxTradesPerDay = account.MaxTradesPerDay,
            DefaultQuantity = account.DefaultQuantity
        };
    }
}
