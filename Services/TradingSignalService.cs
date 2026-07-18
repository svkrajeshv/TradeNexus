using Microsoft.EntityFrameworkCore;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// Service for managing trading signals
/// </summary>
public class TradingSignalService
{
    private readonly TradingDbContext _context;
    private readonly ILogger<TradingSignalService> _logger;

    public TradingSignalService(TradingDbContext context, ILogger<TradingSignalService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new trading signal from parsed signal data
    /// </summary>
    public async Task<int?> CreateSignalAsync(ParsedSignal parsedSignal, long telegramMessageId, DateTime telegramTimestamp)
    {
        try
        {
            var signal = new TradingSignal
            {
                TelegramMessageId = telegramMessageId,
                OriginalMessage = parsedSignal.OriginalMessage,
                TelegramTimestamp = telegramTimestamp,
                ReceivedTimestamp = DateTime.UtcNow,
                ProcessedTimestamp = DateTime.UtcNow,
                Action = parsedSignal.Action,
                Index = parsedSignal.Index,
                Strike = parsedSignal.Strike,
                OptionType = parsedSignal.OptionType,
                EntryPrice = parsedSignal.EntryPrice,
                StopLoss = parsedSignal.StopLoss,
                Targets = parsedSignal.Targets,
                ExpiryDate = parsedSignal.ExpiryDate,
                Status = SignalStatus.Parsed,
                SignalDelayMs = (decimal)(DateTime.UtcNow - telegramTimestamp).TotalMilliseconds
            };

            _context.TradingSignals.Add(signal);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Signal created: {Id} - {Action} {Index} {Strike}{OptionType} @ {Entry}",
                signal.Id, signal.Action, signal.Index, signal.Strike, signal.OptionType, signal.EntryPrice);

            return signal.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating trading signal");
            return null;
        }
    }

    /// <summary>
    /// Gets a signal by ID
    /// </summary>
    public async Task<TradingSignal?> GetSignalAsync(int id)
    {
        try
        {
            return await _context.TradingSignals
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving signal: {Id}", id);
            return null;
        }
    }

    /// <summary>
    /// Gets recent signals (last N)
    /// </summary>
    public async Task<List<TradingSignal>> GetRecentSignalsAsync(int count = 50)
    {
        try
        {
            return await _context.TradingSignals
                .AsNoTracking()
                .OrderByDescending(s => s.ReceivedTimestamp)
                .Take(count)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving recent signals");
            return new List<TradingSignal>();
        }
    }

    /// <summary>
    /// Gets pending signals (not yet executed or ignored)
    /// </summary>
    public async Task<List<TradingSignal>> GetPendingSignalsAsync()
    {
        try
        {
            return await _context.TradingSignals
                .AsNoTracking()
                .Where(s => s.Status == SignalStatus.Pending || s.Status == SignalStatus.Parsed)
                .OrderByDescending(s => s.ReceivedTimestamp)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving pending signals");
            return new List<TradingSignal>();
        }
    }

    /// <summary>
    /// Updates signal status
    /// </summary>
    public async Task<bool> UpdateSignalStatusAsync(int id, SignalStatus status)
    {
        try
        {
            var signal = await _context.TradingSignals.FindAsync(id);
            if (signal == null)
            {
                _logger.LogWarning("Signal not found: {Id}", id);
                return false;
            }

            signal.Status = status;
            signal.ProcessedTimestamp = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            _logger.LogInformation("Signal status updated: {Id} -> {Status}", id, status);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating signal status: {Id}", id);
            return false;
        }
    }

    /// <summary>
    /// Checks if signal already exists (duplicate detection)
    /// </summary>
    public async Task<bool> SignalExistsAsync(long telegramMessageId)
    {
        try
        {
            return await _context.TradingSignals
                .AnyAsync(s => s.TelegramMessageId == telegramMessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking signal existence");
            return false;
        }
    }

    /// <summary>
    /// Gets signals for a specific date
    /// </summary>
    public async Task<List<TradingSignal>> GetSignalsByDateAsync(DateTime date)
    {
        try
        {
            var startOfDay = date.Date;
            var endOfDay = startOfDay.AddDays(1);

            return await _context.TradingSignals
                .AsNoTracking()
                .Where(s => s.ReceivedTimestamp >= startOfDay && s.ReceivedTimestamp < endOfDay)
                .OrderByDescending(s => s.ReceivedTimestamp)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving signals by date");
            return new List<TradingSignal>();
        }
    }

    /// <summary>
    /// Gets signal statistics
    /// </summary>
    public async Task<SignalStatistics> GetStatisticsAsync()
    {
        try
        {
            var totalSignals = await _context.TradingSignals.CountAsync();
            var executedSignals = await _context.TradingSignals.CountAsync(s => s.Status == SignalStatus.Executed);
            var failedSignals = await _context.TradingSignals.CountAsync(s => s.Status == SignalStatus.Failed);
            var ignoredSignals = await _context.TradingSignals.CountAsync(s => s.Status == SignalStatus.Ignored);

            var todaySignals = await _context.TradingSignals
                .Where(s => s.ReceivedTimestamp.Date == DateTime.Today)
                .CountAsync();

            return new SignalStatistics
            {
                TotalSignals = totalSignals,
                ExecutedSignals = executedSignals,
                FailedSignals = failedSignals,
                IgnoredSignals = ignoredSignals,
                TodaySignals = todaySignals
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving signal statistics");
            return new SignalStatistics();
        }
    }
}

/// <summary>
/// Signal statistics DTO
/// </summary>
public class SignalStatistics
{
    public int TotalSignals { get; set; }
    public int ExecutedSignals { get; set; }
    public int FailedSignals { get; set; }
    public int IgnoredSignals { get; set; }
    public int TodaySignals { get; set; }

    public int SuccessRate => TotalSignals == 0 ? 0 : (ExecutedSignals * 100) / TotalSignals;
    public int PendingSignals => TotalSignals - ExecutedSignals - FailedSignals - IgnoredSignals;
}
