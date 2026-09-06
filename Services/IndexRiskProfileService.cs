using NexusApp.Interfaces;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// Resolves segment / index-calibrated StopLoss levels from user settings (Points or Percentage mode).
/// </summary>
public class IndexRiskProfileService(ISettingsService settings) : IIndexRiskProfileService
{
    private readonly ISettingsService _settings = settings;

    private static readonly Dictionary<string, (decimal SlPts, decimal TgtPts, decimal SlPct, decimal TgtPct)> BaselineProfiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // NSE / BSE index options
            ["NIFTY"] = (20m, 35m, 18m, 35m),
            ["BANKNIFTY"] = (40m, 70m, 20m, 40m),
            ["FINNIFTY"] = (20m, 35m, 18m, 35m),
            ["MIDCPNIFTY"] = (15m, 25m, 15m, 30m),
            ["SENSEX"] = (80m, 140m, 25m, 50m),
            ["BANKEX"] = (90m, 150m, 25m, 50m),

            // MCX commodity options. Point values are deliberately NOT copied from the
            // index rows: commodity option premiums sit in a different range entirely,
            // so an index-derived points buffer would be far too wide or too tight.
            // Percent buffers are kept a little wider than equity to absorb the higher
            // intraday volatility of energy contracts.
            ["CRUDEOIL"] = (15m, 30m, 22m, 45m),
            ["NATURALGAS"] = (5m, 10m, 25m, 50m),
            ["GOLD"] = (60m, 120m, 20m, 40m),
            ["SILVER"] = (50m, 100m, 22m, 45m),
        };

    private async Task<IndexRiskProfile> GetProfileAsync(string index)
    {
        var normalized = (index ?? string.Empty).ToUpperInvariant().Trim();
        var (SlPts, TgtPts, SlPct, TgtPct) = BaselineProfiles.TryGetValue(normalized, out var baseVal)
            ? baseVal
            : (SlPts: 25m, TgtPts: 50m, SlPct: 20m, TgtPct: 40m);

        var slPts = await _settings.GetSettingAsync<decimal?>($"SL.Points.{normalized}") ?? SlPts;
        var tgtPts = await _settings.GetSettingAsync<decimal?>($"Target.Points.{normalized}") ?? TgtPts;
        var slPct = await _settings.GetSettingAsync<decimal?>($"SL.Percent.{normalized}") ?? SlPct;
        var tgtPct = await _settings.GetSettingAsync<decimal?>($"Target.Percent.{normalized}") ?? TgtPct;

        return new IndexRiskProfile(normalized, slPts, tgtPts, slPct, tgtPct);
    }

    public async Task<decimal> GetDefaultSlBufferPointsAsync(string? index, decimal entryPrice)
    {
        if (string.IsNullOrWhiteSpace(index))
        {
            return await _settings.GetSettingAsync<decimal?>("DefaultStopLossPoints") ?? 50m;
        }

        var profile = await GetProfileAsync(index);
        var mode = await _settings.GetSettingAsync<string>("Risk.SlMode") ?? "Points";

        if (mode.Equals("Percent", StringComparison.OrdinalIgnoreCase) && entryPrice > 0)
        {
            var calculatedPts = Math.Round(entryPrice * (profile.SlPercent / 100m), 2);
            return Math.Max(0.05m, calculatedPts);
        }

        return profile.DefaultSlPoints > 0 ? profile.DefaultSlPoints : 50m;
    }
}
