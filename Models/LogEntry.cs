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
    public required string Environment { get; init; }
    public required int EventId { get; init; }
    public required string Message { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestId { get; init; }
    public string? OrderId { get; init; }
    public int? DurationMs { get; init; }
    public string? Exception { get; init; }
}
