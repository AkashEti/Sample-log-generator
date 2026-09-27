namespace SampleLogGenerator.Models;

/// <summary>
/// One structured log line. Serialized as a single JSON object per line (JSONL).
/// Optional fields are omitted from the output when null.
/// </summary>
public sealed record LogEntry
{
    public required DateTime Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Service { get; init; }
    public required string Host { get; init; }
    public required string Region { get; init; }
    public required string AvailabilityZone { get; init; }

    /// <summary>Build of the service running on this host at the time (changes with deploys).</summary>
    public required string Version { get; init; }
    public required string Environment { get; init; }
    public required int EventId { get; init; }
    public required string Message { get; init; }

    /// <summary>Distributed trace of one user request (W3C-style, 32 hex chars).</summary>
    public string? TraceId { get; init; }

    /// <summary>This service's span within the trace (16 hex chars).</summary>
    public string? SpanId { get; init; }

    /// <summary>Span of the calling service; absent on the entry span.</summary>
    public string? ParentSpanId { get; init; }

    /// <summary>The service that called this one within the trace.</summary>
    public string? UpstreamService { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestId { get; init; }

    /// <summary>The browsing session that made the request (every request of one visitor shares it).</summary>
    public string? SessionId { get; init; }

    /// <summary>Signed-in customer; absent for guests.</summary>
    public string? UserId { get; init; }
    public string? OrderId { get; init; }
    public int? DurationMs { get; init; }

    /// <summary>Timing of an operation that timed out or completed after its caller gave up.</summary>
    public OperationInfo? Operation { get; init; }
    public string? Exception { get; init; }
}

public static class OperationOutcomes
{
    /// <summary>The caller stopped waiting (logged by the caller at <see cref="OperationInfo.TimeoutAt"/>).</summary>
    public const string Timeout = "Timeout";

    /// <summary>The callee finished successfully after the caller had already timed out.</summary>
    public const string LateSuccess = "LateSuccess";

    /// <summary>The callee failed after the caller had already timed out.</summary>
    public const string LateFailure = "LateFailure";
}

/// <summary>
/// Makes "work finished after the caller gave up" explicit. The log's own timestamp is when the outcome was observed;
/// for late outcomes it is also the completion time.
/// </summary>
/// <param name="Name">Operation, as Client.Method (e.g. PaymentClient.Charge).</param>
/// <param name="StartedAt">When the caller started this attempt.</param>
/// <param name="Outcome">One of <see cref="OperationOutcomes"/>.</param>
/// <param name="TimeoutAt">When the caller gave up (for Timeout, equal to the log timestamp).</param>
/// <param name="Attempt">Attempt number when the caller retries.</param>
public sealed record OperationInfo(string Name, DateTime StartedAt, string Outcome, DateTime? TimeoutAt = null, int? Attempt = null);
