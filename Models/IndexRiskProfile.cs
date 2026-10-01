namespace NexusApp.Models;

/// <summary>
/// Configuration and calculated risk parameters for an index / segment.
/// </summary>
public record IndexRiskProfile(
    string Index,
    decimal DefaultSlPoints,
    decimal DefaultTargetPoints,
    decimal OverrideSlPoints = 0m,
    decimal OverrideTargetPoints = 15m);
