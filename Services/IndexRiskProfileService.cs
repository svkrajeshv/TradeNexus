using NexusApp.Helpers;
using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// Resolves segment / index-calibrated StopLoss levels from user settings (Points or Percentage mode).
/// </summary>
public class IndexRiskProfileService(ISettingsService settings) : IIndexRiskProfileService
{
    private readonly ISettingsService _settings = settings;

    private static readonly Dictionary<string, (decimal SlPts, decimal TgtPts, decimal OverrideSlPts, decimal OverrideTgtPts)> BaselineProfiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // NSE / BSE index options
            ["NIFTY"] = (20m, 35m, 0m, 15m),
            ["BANKNIFTY"] = (40m, 70m, 0m, 15m),
            ["FINNIFTY"] = (20m, 35m, 0m, 15m),
            ["MIDCPNIFTY"] = (15m, 25m, 0m, 15m),
            ["SENSEX"] = (80m, 140m, 0m, 15m),
            ["BANKEX"] = (90m, 150m, 0m, 15m),

            // MCX commodity options. Point values are deliberately NOT copied from the
            // index rows: commodity option premiums sit in a different range entirely,
            // so an index-derived points buffer would be far too wide or too tight.
            ["CRUDEOIL"] = (15m, 30m, 0m, 15m),
            ["NATURALGAS"] = (5m, 10m, 0m, 15m),
            ["GOLD"] = (60m, 120m, 0m, 15m),
            ["SILVER"] = (50m, 100m, 0m, 15m),

            // Mini / micro contracts. Premiums are quoted on the same scale as the
            // full-size contract, so the point buffers match; only the lot size differs.
            ["CRUDEOILM"] = (15m, 30m, 0m, 15m),
            ["NATGASMINI"] = (5m, 10m, 0m, 15m),
            ["GOLDM"] = (60m, 120m, 0m, 15m),
            ["SILVERM"] = (50m, 100m, 0m, 15m),
            ["SILVERMIC"] = (50m, 100m, 0m, 15m),
        };

    public async Task<IndexRiskProfile> GetProfileAsync(string? index)
    {
        var normalized = string.IsNullOrWhiteSpace(index) ? "NIFTY" : MarketSegments.NormalizeUnderlying(index);
        var (SlPts, TgtPts, OverrideSlPts, OverrideTgtPts) = BaselineProfiles.TryGetValue(normalized, out var baseVal)
            ? baseVal
            : (SlPts: 25m, TgtPts: 50m, OverrideSlPts: 0m, OverrideTgtPts: 15m);

        var baseUnderlying = normalized switch
        {
            "CRUDEOILM" => "CRUDEOIL",
            "NATGASMINI" => "NATURALGAS",
            "GOLDM" => "GOLD",
            "SILVERM" or "SILVERMIC" => "SILVER",
            _ => normalized
        };

        var slPts = await _settings.GetSettingAsync<decimal?>($"SL.Points.{normalized}")
            ?? (normalized != baseUnderlying ? await _settings.GetSettingAsync<decimal?>($"SL.Points.{baseUnderlying}") : null)
            ?? SlPts;

        var tgtPts = await _settings.GetSettingAsync<decimal?>($"Target.Points.{normalized}")
            ?? (normalized != baseUnderlying ? await _settings.GetSettingAsync<decimal?>($"Target.Points.{baseUnderlying}") : null)
            ?? TgtPts;

        var overrideSl = await _settings.GetSettingAsync<decimal?>($"SL.Override.{normalized}")
            ?? (normalized != baseUnderlying ? await _settings.GetSettingAsync<decimal?>($"SL.Override.{baseUnderlying}") : null)
            ?? OverrideSlPts;

        var overrideTgt = await _settings.GetSettingAsync<decimal?>($"Target.Override.{normalized}")
            ?? (normalized != baseUnderlying ? await _settings.GetSettingAsync<decimal?>($"Target.Override.{baseUnderlying}") : null)
            ?? OverrideTgtPts;

        return new IndexRiskProfile(normalized, slPts, tgtPts, overrideSl, overrideTgt);
    }

    public async Task<decimal> GetOverrideSlPointsAsync(string? index)
    {
        if (string.IsNullOrWhiteSpace(index))
            return 0m;

        var profile = await GetProfileAsync(index);
        return profile.OverrideSlPoints;
    }

    public async Task<decimal> GetOverrideTargetPointsAsync(string? index)
    {
        if (string.IsNullOrWhiteSpace(index))
            return 15m;

        var profile = await GetProfileAsync(index);
        return profile.OverrideTargetPoints;
    }

    public async Task<decimal> GetDefaultSlBufferPointsAsync(string? index, decimal entryPrice)
    {
        if (string.IsNullOrWhiteSpace(index))
        {
            return await _settings.GetSettingAsync<decimal?>("DefaultStopLossPoints") ?? 50m;
        }

        var profile = await GetProfileAsync(index);
        if (profile.OverrideSlPoints > 0)
        {
            return profile.OverrideSlPoints;
        }

        return profile.DefaultSlPoints > 0 ? profile.DefaultSlPoints : 50m;
    }
}
