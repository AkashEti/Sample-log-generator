namespace SampleLogGenerator.Models;

/// <summary>
/// A point-in-time metric value for a service (backs a future get_metrics tool).
/// </summary>
public sealed record MetricSample
{
    public required DateTime Timestamp { get; init; }
    public required string Service { get; init; }
    public required string Metric { get; init; }
    public required double Value { get; init; }
    public required string Unit { get; init; }
    public required string Environment { get; init; }
}
