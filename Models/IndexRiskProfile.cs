namespace NexusApp.Models;

/// <summary>
/// Configuration and calculated risk parameters for an index / segment.
/// </summary>
public record IndexRiskProfile(
    string Index,
    decimal DefaultSlPoints,
    decimal DefaultTargetPoints,
    decimal SlPercent,
    decimal TargetPercent);
