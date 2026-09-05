namespace NexusApp.Interfaces;

public interface IIndexRiskProfileService
{
    Task<decimal> GetDefaultSlBufferPointsAsync(string? index, decimal entryPrice);
}
