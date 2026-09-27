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

    /// <summary>One of <see cref="MetricTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>For counters and window aggregates: the length of the window ending at <see cref="Timestamp"/>.</summary>
    public int? WindowSeconds { get; init; }
    public required string Environment { get; init; }
}

public static class MetricTypes
{
    /// <summary>Number of events in the window (not cumulative).</summary>
    public const string Counter = "counter";

    /// <summary>A statistic computed over the window, such as a p95 latency or an error rate.</summary>
    public const string WindowAggregate = "window_aggregate";

    /// <summary>An instantaneous reading at the timestamp, such as CPU or pool usage.</summary>
    public const string Gauge = "gauge";
}
