using System.Text.Json;
using SampleLogGenerator.Configuration;
using SampleLogGenerator.Models;
using SampleLogGenerator.Output;
using SampleLogGenerator.Simulation;

namespace SampleLogGenerator.Hosting;

public sealed record DatasetRequest(
    string? Name = null,
    int DurationMinutes = 60,
    int Incidents = 5,
    int? MinUsers = null,
    int? MaxUsers = null,
    int? Seed = null,
    List<IncidentScenario>? Scenarios = null,
    bool IncludePromptInjection = false,
    DateTime? StartTime = null,
    int? Distractors = null);

public sealed record DatasetResult(
    string Name,
    string Directory,
    int Seed,
    string Users,
    DateTime StartTime,
    DateTime EndTime,
    int LogCount,
    int MetricCount,
    IReadOnlyList<IncidentRecord> Incidents);

/// <summary>
/// Generates a historical dataset in simulated time (no waiting): logs, metrics and ground-truth incidents.
/// The same seed and parameters always produce the same dataset, which makes evaluation runs repeatable.
/// </summary>
public static class DatasetGenerator
{
    public static DatasetResult Generate(DatasetRequest request, LogGeneratorOptions baseOptions, string datasetsRoot)
    {
        if (request.DurationMinutes is < 1 or > 24 * 60)
            throw new ArgumentException("DurationMinutes must be between 1 and 1440.");
        if (request.Incidents is < 0 or > 200)
            throw new ArgumentException("Incidents must be between 0 and 200.");

        var options = baseOptions.Clone();
        options.AutoIncidents = false;
        if (request.MinUsers is { } minUsers) options.MinConcurrentUsers = minUsers;
        if (request.MaxUsers is { } maxUsers) options.MaxConcurrentUsers = maxUsers;
        if (options.MinConcurrentUsers < 1 || options.MaxConcurrentUsers < options.MinConcurrentUsers || options.MaxConcurrentUsers > 5000)
            throw new ArgumentException("Users must satisfy 1 <= min <= max <= 5000.");
        if (request.Distractors is { } distractors) options.DistractorsPerIncident = distractors;
        if (options.DistractorsPerIncident is < 0 or > 6)
            throw new ArgumentException("Distractors must be between 0 and 6.");

        var seed = request.Seed ?? Random.Shared.Next();
        var generator = new LogGenerator(options, seed);
        var planner = new Random(seed ^ 0x5eed);

        var duration = TimeSpan.FromMinutes(request.DurationMinutes);
        var start = DateTime.SpecifyKind(request.StartTime?.ToUniversalTime() ?? TruncateToMinute(DateTime.UtcNow) - duration, DateTimeKind.Utc);
        var end = start + duration;

        var name = string.IsNullOrWhiteSpace(request.Name) ? $"dataset-{start:yyyyMMdd-HHmm}-seed{seed}" : request.Name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
            throw new ArgumentException("Name contains invalid characters.");
        var directory = Path.GetFullPath(Path.Combine(datasetsRoot, name));
        System.IO.Directory.CreateDirectory(directory);
        foreach (var file in new[] { "logs.jsonl", "metrics.jsonl", "incidents.jsonl", "manifest.json" })
            File.Delete(Path.Combine(directory, file)); // regenerate from scratch; writers append

        var plan = PlanIncidents(request, options, planner, start, duration);
        var logCount = 0;
        var metricCount = 0;
        var incidents = new List<IncidentRecord>();

        using (var logs = new JsonlWriter(Path.Combine(directory, "logs.jsonl")))
        using (var metrics = new JsonlWriter(Path.Combine(directory, "metrics.jsonl")))
        using (var groundTruth = new JsonlWriter(Path.Combine(directory, "incidents.jsonl")))
        {
            void Write(GeneratorBatch batch)
            {
                logs.WriteMany(batch.Logs);
                metrics.WriteMany(batch.Metrics);
                groundTruth.WriteMany(batch.CompletedIncidents);
                logCount += batch.Logs.Count;
                metricCount += batch.Metrics.Count;
                incidents.AddRange(batch.CompletedIncidents);
            }

            var next = 0;
            for (var t = start; t <= end; t = t.AddSeconds(1))
            {
                while (next < plan.Count && plan[next].StartAt <= t)
                {
                    var (scenario, startAt, incidentDuration) = plan[next++];
                    generator.StartIncident(scenario, startAt, incidentDuration);
                }
                Write(generator.Advance(t));
            }
            Write(generator.Drain(end));
        }

        var result = new DatasetResult(name, directory, seed, $"{options.MinConcurrentUsers}-{options.MaxConcurrentUsers}", start, end, logCount, metricCount, incidents);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            result.Name,
            result.Seed,
            result.StartTime,
            result.EndTime,
            result.Users,
            options.Environment,
            result.LogCount,
            result.MetricCount,
            options.DistractorsPerIncident,
            Incidents = incidents.Select(i => new { i.IncidentId, i.Scenario, i.Difficulty, i.StartedAt, i.EndedAt }),
        }, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }));

        return result;
    }

    /// <summary>Spreads incidents evenly over the window, one per slot, with quiet periods in between.</summary>
    private static List<(IncidentScenario Scenario, DateTime StartAt, TimeSpan Duration)> PlanIncidents(
        DatasetRequest request, LogGeneratorOptions options, Random rng, DateTime start, TimeSpan window)
    {
        var scenarios = request.Scenarios is { Count: > 0 }
            ? request.Scenarios.Distinct().ToList()
            : Enum.GetValues<IncidentScenario>().Where(s => s != IncidentScenario.PromptInjectionAttempt).ToList();
        if (request.IncludePromptInjection && !scenarios.Contains(IncidentScenario.PromptInjectionAttempt))
            scenarios.Add(IncidentScenario.PromptInjectionAttempt);

        var plan = new List<(IncidentScenario, DateTime, TimeSpan)>();
        if (request.Incidents == 0 || scenarios.Count == 0) return plan;

        var order = scenarios.OrderBy(_ => rng.Next()).ToList();
        var slot = window / request.Incidents;
        for (var i = 0; i < request.Incidents; i++)
        {
            var configured = TimeSpan.FromSeconds(rng.Next(options.MinIncidentDurationSeconds, Math.Max(options.MinIncidentDurationSeconds, options.MaxIncidentDurationSeconds) + 1));
            var duration = configured < slot * 0.5 ? configured : slot * 0.5;
            var offset = slot * (0.15 + rng.NextDouble() * 0.25);
            plan.Add((order[i % order.Count], start + slot * i + offset, duration));
        }
        return plan;
    }

    private static DateTime TruncateToMinute(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
}
