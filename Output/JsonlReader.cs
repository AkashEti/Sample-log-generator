using System.Text.Json;
using SampleLogGenerator.Models;
using SampleLogGenerator.Simulation;

namespace SampleLogGenerator.Output;

public sealed record LogQuery(
    string? Service = null,
    string? Level = null,
    string? MinLevel = null,
    string? OrderId = null,
    string? CorrelationId = null,
    string? Q = null,
    DateTime? From = null,
    DateTime? To = null,
    int? Take = null)
{
    public bool Matches(LogEntry e) =>
        (Service is null || e.Service.Equals(Service, StringComparison.OrdinalIgnoreCase)) &&
        (Level is null || e.Level.Equals(Level, StringComparison.OrdinalIgnoreCase)) &&
        (MinLevel is null || Levels.Rank(e.Level) >= Levels.Rank(MinLevel)) &&
        (OrderId is null || string.Equals(e.OrderId, OrderId, StringComparison.OrdinalIgnoreCase)) &&
        (CorrelationId is null || string.Equals(e.CorrelationId, CorrelationId, StringComparison.OrdinalIgnoreCase)) &&
        (Q is null || e.Message.Contains(Q, StringComparison.OrdinalIgnoreCase) || (e.Exception?.Contains(Q, StringComparison.OrdinalIgnoreCase) ?? false)) &&
        (From is null || e.Timestamp >= From.Value.ToUniversalTime()) &&
        (To is null || e.Timestamp <= To.Value.ToUniversalTime());
}

public sealed record MetricQuery(string? Service = null, string? Metric = null, DateTime? From = null, DateTime? To = null, int? Take = null)
{
    public bool Matches(MetricSample m) =>
        (Service is null || m.Service.Equals(Service, StringComparison.OrdinalIgnoreCase)) &&
        (Metric is null || m.Metric.Equals(Metric, StringComparison.OrdinalIgnoreCase)) &&
        (From is null || m.Timestamp >= From.Value.ToUniversalTime()) &&
        (To is null || m.Timestamp <= To.Value.ToUniversalTime());
}

public static class JsonlReader
{
    public const int DefaultTake = 200;
    public const int MaxTake = 5000;

    public static IReadOnlyList<LogEntry> SearchLogs(string path, LogQuery query) =>
        Search<LogEntry>(path, query.Take, query.Matches, PreFilter(query.OrderId, query.CorrelationId));

    public static IReadOnlyList<MetricSample> SearchMetrics(string path, MetricQuery query) =>
        Search<MetricSample>(path, query.Take, query.Matches, PreFilter(query.Metric));

    public static IEnumerable<T> ReadAll<T>(string path)
    {
        foreach (var line in ReadLines(path))
        {
            var item = TryDeserialize<T>(line);
            if (item is not null) yield return item;
        }
    }

    /// <summary>Returns the most recent <paramref name="take"/> matches, oldest first.</summary>
    private static List<T> Search<T>(string path, int? take, Func<T, bool> matches, Func<string, bool> preFilter)
    {
        var limit = Math.Clamp(take ?? DefaultTake, 1, MaxTake);
        var results = new Queue<T>(limit + 1);
        foreach (var line in ReadLines(path))
        {
            if (!preFilter(line)) continue;
            var item = TryDeserialize<T>(line);
            if (item is null || !matches(item)) continue;
            results.Enqueue(item);
            if (results.Count > limit) results.Dequeue();
        }
        return [.. results];
    }

    /// <summary>Cheap substring check on the raw line before paying for JSON deserialization.</summary>
    private static Func<string, bool> PreFilter(params string?[] mustContain)
    {
        var terms = mustContain.Where(t => !string.IsNullOrEmpty(t)).ToArray();
        return line => terms.All(t => line.Contains(t!, StringComparison.OrdinalIgnoreCase));
    }

    private static T? TryDeserialize<T>(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return default; // e.g. a line that is being written right now
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        if (!File.Exists(path)) yield break;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0) yield return line;
        }
    }
}
