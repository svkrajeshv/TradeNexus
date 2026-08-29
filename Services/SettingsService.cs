using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NexusApp.Data;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// Service for managing application settings stored in the database
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly TradingDbContext _context;
    private readonly ILogger<SettingsService> _logger;
    private readonly Dictionary<string, ApplicationSetting> _cache = new();

    public SettingsService(TradingDbContext context, ILogger<SettingsService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<T?> GetSettingAsync<T>(string key)
    {
        try
        {
            if (_cache.TryGetValue(key, out var cached))
                return ConvertValue<T>(cached.Value, cached.Type);

            var setting = await _context.ApplicationSettings
                .FirstOrDefaultAsync(s => s.Key == key);

            if (setting == null)
            {
                _logger.LogDebug("Setting not found: {Key} (using default)", key);
                return default;
            }

            _cache[key] = setting;
            return ConvertValue<T>(setting.Value, setting.Type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving setting: {Key}", key);
            return default;
        }
    }

    public async Task SetSettingAsync<T>(string key, T value)
    {
        try
        {
            var setting = await _context.ApplicationSettings
                .FirstOrDefaultAsync(s => s.Key == key);

            var stringValue = ConvertToString(value);
            var settingType = DetermineType(value);

            if (setting == null)
            {
                setting = new ApplicationSetting(key, stringValue, settingType);
                _context.ApplicationSettings.Add(setting);
            }
            else
            {
                setting.Value = stringValue;
                setting.Type = settingType;
                setting.LastModified = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            _cache[key] = setting;
            
            _logger.LogInformation("Setting saved: {Key} = {Value}", key, stringValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving setting: {Key}", key);
        }
    }

    public async Task<Dictionary<string, string>> GetAllSettingsAsync()
    {
        try
        {
            var settings = await _context.ApplicationSettings.ToListAsync();
            return settings.ToDictionary(s => s.Key, s => s.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all settings");
            return new Dictionary<string, string>();
        }
    }

    private static T? ConvertValue<T>(string value, SettingType type)
    {
        try
        {
            return type switch
            {
                SettingType.String => (T)(object)value,
                SettingType.Integer => (T)(object)int.Parse(value),
                SettingType.Decimal => (T)(object)decimal.Parse(value),
                SettingType.Boolean => (T)(object)bool.Parse(value),
                SettingType.Json => JsonSerializer.Deserialize<T>(value) ?? default,
                _ => default
            };
        }
        catch
        {
            return default;
        }
    }

    private static string ConvertToString<T>(T? value)
    {
        if (value == null)
            return string.Empty;

        return value switch
        {
            string s => s,
            bool b => b.ToString(),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static SettingType DetermineType<T>(T? value)
    {
        if (value == null)
            return SettingType.String;

        return value switch
        {
            bool => SettingType.Boolean,
            int => SettingType.Integer,
            decimal => SettingType.Decimal,
            string => SettingType.String,
            _ => SettingType.Json
        };
    }
}
