using NexusApp.Helpers;

namespace NexusApp.Models;

/// <summary>
/// Per-underlying lot override edited on the Settings page. Persisted as
/// <c>Lots.{Underlying}</c>; 0 means "use DefaultQuantity".
/// </summary>
public class LotConfigVm
{
    public string Underlying { get; set; } = string.Empty;
    public MarketSegment Segment { get; set; }
    public int Lots { get; set; }
}

/// <summary>
/// Per-underlying default Stop-Loss / Target levels edited on the Settings page.
/// Persisted as <c>SL.Points.{Index}</c>, <c>Target.Points.{Index}</c>,
/// <c>SL.Percent.{Index}</c> and <c>Target.Percent.{Index}</c>.
/// </summary>
public class IndexRiskProfileVm
{
    public string Index { get; set; } = string.Empty;
    public MarketSegment Segment { get; set; }
    public decimal DefaultSlPoints { get; set; }
    public decimal DefaultTargetPoints { get; set; }
    public decimal SlPercent { get; set; }
    public decimal TargetPercent { get; set; }
}
