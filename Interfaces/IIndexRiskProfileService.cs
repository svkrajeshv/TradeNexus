using NexusApp.Models;

namespace NexusApp.Interfaces;

public interface IIndexRiskProfileService
{
    Task<decimal> GetDefaultSlBufferPointsAsync(string? index, decimal entryPrice);
    Task<decimal> GetOverrideTargetPointsAsync(string? index);
    Task<IndexRiskProfile> GetProfileAsync(string? index);
}
