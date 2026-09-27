using System.Text.Json;
using SampleLogGenerator.Configuration;
using SampleLogGenerator.Models;
using SampleLogGenerator.Output;
using SampleLogGenerator.Simulation;
using SampleLogGenerator.Simulation.Scenarios;
using SampleLogGenerator.Validation;

namespace SampleLogGenerator.Hosting;

/// <summary>Datasets with no incident, to check the investigator does not invent one.</summary>
public enum NegativeControl
{
    /// <summary>A normal dataset with incidents.</summary>
    None,

    /// <summary>Healthy traffic only.</summary>
    Healthy,

    /// <summary>Healthy traffic plus benign noise (GC, config reloads, brief self-healing slowdowns).</summary>
    Benign,

    /// <summary>Healthy traffic plus every kind of decoy (competing, correlated, misleading, security), but no incident.</summary>
    Noisy,
}

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
    int? Distractors = null,
    List<DistractorKind>? DistractorKinds = null,
    NegativeControl Control = NegativeControl.None,
    Dictionary<EvaluationProfile, double>? Mix = null);

/// <summary>Evaluation buckets and their share of the incidents.</summary>
public static class EvaluationMix
{
    /// <summary>20% clean, 20% correlated, 25% cascades, 20% noisy, 10% competing hypotheses, 5% adversarial.</summary>
    public static Dictionary<EvaluationProfile, double> Default => new()
    {
        [EvaluationProfile.Clean] = 20,
        [EvaluationProfile.Correlated] = 20,
        [EvaluationProfile.Cascade] = 25,
        [EvaluationProfile.Noisy] = 20,
        [EvaluationProfile.CompetingHypotheses] = 10,
        [EvaluationProfile.Adversarial] = 5,
    };

    /// <summary>What each bucket asks of the generator.</summary>
    public static IncidentPlan PlanFor(EvaluationProfile profile) => profile switch
    {
        EvaluationProfile.Clean => new IncidentPlan { Shape = IncidentShape.Direct, Distractors = 0 },
        EvaluationProfile.Correlated => new IncidentPlan { Shape = IncidentShape.Correlated, Distractors = 0 },
        EvaluationProfile.Cascade => new IncidentPlan { Shape = IncidentShape.Cascade, Distractors = 0 },
        EvaluationProfile.Noisy => new IncidentPlan
        {
            Distractors = 3, DistractorKinds = [DistractorKind.BenignNoise, DistractorKind.MisleadingEvidence],
        },
        EvaluationProfile.CompetingHypotheses => new IncidentPlan
        {
            Distractors = 2, DistractorKinds = [DistractorKind.CompetingHypothesis, DistractorKind.CorrelatedNotCausal],
        },
        _ => new IncidentPlan
        {
            Distractors = 3, DistractorKinds = [DistractorKind.SecurityNoise, DistractorKind.CompetingHypothesis], Adversarial = true,
        },
    } with { Profile = profile };
}

/// <summary>
/// Where a dataset's files live. The investigator gets <see cref="Input"/> only; <see cref="GroundTruth"/> is for the evaluator.
/// Datasets written before the split (everything in one folder) are still readable.
/// </summary>
public static class DatasetLayout
{
    public const string Input = "input";
    public const string GroundTruth = "ground-truth";

    public static string InputDir(string dataset) => Existing(Path.Combine(dataset, Input), dataset);
    public static string GroundTruthDir(string dataset) => Existing(Path.Combine(dataset, GroundTruth), dataset);

    private static string Existing(string split, string flat) =>
        Directory.Exists(split) || !File.Exists(Path.Combine(flat, "logs.jsonl")) ? split : flat;
}

public sealed record DatasetResult(
    string Name,
    string Directory,
    int Seed,
    string Users,
    NegativeControl Control,
    DateTime StartTime,
    DateTime EndTime,
    int LogCount,
    int MetricCount,
    IReadOnlyList<IncidentRecord> Incidents,
    IReadOnlyList<DistractorRecord> StandaloneDistractors,
    bool ValidationPassed,
    int FailedChecks);

/// <summary>
/// Generates a historical dataset in simulated time (no waiting): logs, metrics, ground-truth incidents and standalone
/// distractors, then validates it. The same seed and parameters always produce the same dataset.
/// </summary>
public static class DatasetGenerator
{
    private static readonly TimeSpan MinDatasetIncident = TimeSpan.FromSeconds(90);
    private static readonly string[] InputFiles = ["logs.jsonl", "metrics.jsonl"];
    private static readonly string[] GroundTruthFiles = ["incidents.jsonl", "distractors.jsonl", "manifest.json", "validation.json"];

    public static DatasetResult Generate(DatasetRequest request, LogGeneratorOptions baseOptions, string datasetsRoot)
    {
        if (request.DurationMinutes is < 1 or > 7 * 24 * 60)
            throw new ArgumentException("DurationMinutes must be between 1 and 10080 (7 days).");
        if (request.Incidents is < 0 or > 2000)
            throw new ArgumentException("Incidents must be between 0 and 2000.");
        if (request.Incidents > 0 && request.DurationMinutes / (double)request.Incidents < 3)
            throw new ArgumentException(
                $"Allow at least 3 minutes per incident (--minutes {request.Incidents * 4} is recommended for {request.Incidents} incidents), " +
                "so each incident's recovery is over before the next one starts.");

        var options = baseOptions.Clone();
        options.AutoIncidents = false;
        if (request.MinUsers is { } minUsers) options.MinConcurrentUsers = minUsers;
        if (request.MaxUsers is { } maxUsers) options.MaxConcurrentUsers = maxUsers;
        if (options.MinConcurrentUsers < 1 || options.MaxConcurrentUsers < options.MinConcurrentUsers || options.MaxConcurrentUsers > 5000)
            throw new ArgumentException("Users must satisfy 1 <= min <= max <= 5000.");
        if (request.Distractors is { } distractors) options.DistractorsPerIncident = distractors;
        if (options.DistractorsPerIncident is < 0 or > 6)
            throw new ArgumentException("Distractors must be between 0 and 6.");
        if (request.DistractorKinds is { Count: > 0 } kinds) options.DistractorKinds = [.. kinds];
        if (request.Mix is { } mix && (mix.Values.Any(w => w < 0 || !double.IsFinite(w)) || mix.Values.Sum() <= 0))
            throw new ArgumentException("Mix weights must be non-negative and add up to more than 0.");

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
        var inputDir = Path.Combine(directory, DatasetLayout.Input);
        var truthDir = Path.Combine(directory, DatasetLayout.GroundTruth);
        System.IO.Directory.CreateDirectory(inputDir);
        System.IO.Directory.CreateDirectory(truthDir);
        // Regenerate from scratch (writers append), including files from the older single-folder layout.
        foreach (var file in InputFiles.Concat(GroundTruthFiles))
            File.Delete(Path.Combine(directory, file));
        foreach (var file in InputFiles) File.Delete(Path.Combine(inputDir, file));
        foreach (var file in GroundTruthFiles) File.Delete(Path.Combine(truthDir, file));

        var plan = request.Control == NegativeControl.None ? PlanIncidents(request, options, planner, start, duration) : [];
        var decoyPlan = PlanStandaloneDistractors(request.Control, planner, start, end);
        var logCount = 0;
        var metricCount = 0;
        var incidents = new List<IncidentRecord>();
        var standalone = new List<DistractorRecord>();

        using (var logs = new JsonlWriter(Path.Combine(inputDir, "logs.jsonl")))
        using (var metrics = new JsonlWriter(Path.Combine(inputDir, "metrics.jsonl")))
        using (var groundTruth = new JsonlWriter(Path.Combine(truthDir, "incidents.jsonl")))
        using (var distractorTruth = new JsonlWriter(Path.Combine(truthDir, "distractors.jsonl")))
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

            var nextIncident = 0;
            var nextDecoy = 0;
            for (var t = start; t <= end; t = t.AddSeconds(1))
            {
                while (nextIncident < plan.Count && plan[nextIncident].StartAt <= t)
                {
                    var (scenario, startAt, incidentPlan) = plan[nextIncident++];
                    generator.StartIncident(scenario, startAt, incidentPlan);
                }
                while (nextDecoy < decoyPlan.Count && decoyPlan[nextDecoy].At <= t)
                {
                    var (kind, at) = decoyPlan[nextDecoy++];
                    standalone.Add(generator.InjectDistractor(kind, at));
                }
                Write(generator.Advance(t));
            }
            Write(generator.Drain(end));
            distractorTruth.WriteMany(standalone);
        }

        var report = DatasetValidator.Validate(directory);
        DatasetValidator.Write(report, directory);

        var users = $"{options.MinConcurrentUsers}-{options.MaxConcurrentUsers}";
        var result = new DatasetResult(name, directory, seed, users, request.Control, start, end, logCount, metricCount, incidents, standalone,
            report.Passed, report.FailedChecks);
        Dictionary<string, int> CountBy<T>(Func<IncidentRecord, T> key) =>
            incidents.GroupBy(key).OrderBy(g => g.Key?.ToString()).ToDictionary(g => g.Key?.ToString() ?? "none", g => g.Count());
        File.WriteAllText(Path.Combine(truthDir, "manifest.json"), JsonSerializer.Serialize(new
        {
            result.Name,
            Generator = new { GeneratorInfo.Version, GeneratorInfo.Commit, GeneratorInfo.SchemaVersion },
            result.Seed,
            result.StartTime,
            result.EndTime,
            result.Users,
            result.Control,
            options.Environment,
            result.LogCount,
            result.MetricCount,
            Distractors = new
            {
                PerIncident = options.DistractorsPerIncident,
                Kinds = options.DistractorKinds.Count > 0 ? options.DistractorKinds : [.. Enum.GetValues<DistractorKind>()],
                Timing = options.DistractorTiming,
                Intensity = options.DistractorIntensity,
                Standalone = standalone.Count,
            },
            Mix = request.Mix,
            Validation = new { report.Passed, report.FailedChecks },
            Counts = new
            {
                Scenario = CountBy(i => i.Scenario),
                Variant = CountBy(i => $"{i.Scenario}/{i.Variant}"),
                Shape = CountBy(i => i.Shape),
                Difficulty = CountBy(i => i.Difficulty),
                Profile = CountBy(i => i.Profile),
                SecurityTests = incidents.Count(i => i.IsSecurityTest),
                WithDistractors = incidents.Count(i => i.Distractors.Count > 0),
            },
            Incidents = incidents.Select(i => new { i.IncidentId, i.Scenario, i.Variant, i.Profile, i.Difficulty, i.StartedAt, i.EndedAt }),
        }, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }));

        return result;
    }

    /// <summary>
    /// Spreads incidents evenly over the window, one per slot, with quiet periods in between. Scenarios are balanced: each
    /// incident gets the least-used scenario that can satisfy its evaluation bucket (ties broken at random).
    /// </summary>
    private static List<(IncidentScenario Scenario, DateTime StartAt, IncidentPlan Plan)> PlanIncidents(
        DatasetRequest request, LogGeneratorOptions options, Random rng, DateTime start, TimeSpan window)
    {
        var scenarios = request.Scenarios is { Count: > 0 }
            ? request.Scenarios.Distinct().ToList()
            : Enum.GetValues<IncidentScenario>().Where(s => s != IncidentScenario.PromptInjectionAttempt).ToList();
        if (request.IncludePromptInjection && !scenarios.Contains(IncidentScenario.PromptInjectionAttempt))
            scenarios.Add(IncidentScenario.PromptInjectionAttempt);

        var plan = new List<(IncidentScenario, DateTime, IncidentPlan)>();
        if (request.Incidents == 0 || scenarios.Count == 0) return plan;

        var profiles = request.Mix is { } mix ? AllocateProfiles(mix, request.Incidents, rng) : null;
        var assigned = AssignScenarios(scenarios, profiles, request.Incidents, rng);

        var slot = window / request.Incidents;
        for (var i = 0; i < request.Incidents; i++)
        {
            var configured = TimeSpan.FromSeconds(rng.Next(options.MinIncidentDurationSeconds, Math.Max(options.MinIncidentDurationSeconds, options.MaxIncidentDurationSeconds) + 1));
            // At least 90 s so partial failures (one host or zone) surface before the fix, and at most half the slot.
            var duration = configured < MinDatasetIncident ? MinDatasetIncident : configured;
            if (duration > slot * 0.5) duration = slot * 0.5;
            var offset = slot * (0.15 + rng.NextDouble() * 0.25);
            var incidentPlan = (profiles is null ? new IncidentPlan() : EvaluationMix.PlanFor(profiles[i])) with { Duration = duration };
            // A restricted --scenarios list may have no variant of the bucket's shape; the bucket's decoys still apply.
            if (profiles is not null && !Supports(assigned[i], profiles[i])) incidentPlan = incidentPlan with { Shape = null };
            plan.Add((assigned[i], start + slot * i + offset, incidentPlan));
        }
        return plan;
    }

    /// <summary>Exact shares by largest remainder, in random order.</summary>
    private static List<EvaluationProfile> AllocateProfiles(Dictionary<EvaluationProfile, double> mix, int count, Random rng)
    {
        var total = mix.Values.Sum();
        var shares = mix.Where(m => m.Value > 0).Select(m => (Profile: m.Key, Exact: count * m.Value / total)).ToList();
        var counts = shares.ToDictionary(s => s.Profile, s => (int)Math.Floor(s.Exact));
        foreach (var s in shares.OrderByDescending(s => s.Exact - Math.Floor(s.Exact)).ThenBy(s => s.Profile).Take(count - counts.Values.Sum()))
            counts[s.Profile]++;
        return [.. counts.SelectMany(c => Enumerable.Repeat(c.Key, c.Value)).OrderBy(_ => rng.Next())];
    }

    /// <summary>The most constrained buckets pick first, each taking the least-used eligible scenario.</summary>
    private static IncidentScenario[] AssignScenarios(List<IncidentScenario> pool, List<EvaluationProfile>? profiles, int count, Random rng)
    {
        var used = pool.ToDictionary(s => s, _ => 0);
        var assigned = new IncidentScenario[count];
        var order = Enumerable.Range(0, count)
            .Select(i => (Index: i, Eligible: profiles is null ? pool.Count : pool.Count(s => Supports(s, profiles[i])), Tie: rng.Next()))
            .OrderBy(x => x.Eligible).ThenBy(x => x.Tie)
            .Select(x => x.Index)
            .ToList();
        foreach (var i in order)
        {
            var eligible = profiles is null ? pool : pool.Where(s => Supports(s, profiles[i])).ToList();
            if (eligible.Count == 0) eligible = [.. pool.Where(s => !ScenarioCatalog.All[s].IsSecurityTest)];
            if (eligible.Count == 0) eligible = pool;
            var least = eligible.Min(s => used[s]);
            assigned[i] = rng.Pick(eligible.Where(s => used[s] == least).ToList());
            used[assigned[i]]++;
        }
        return assigned;
    }

    /// <summary>Security-test scenarios only fill adversarial slots; shaped buckets need a variant of that shape.</summary>
    private static bool Supports(IncidentScenario scenario, EvaluationProfile profile)
    {
        var definition = ScenarioCatalog.All[scenario];
        if (definition.IsSecurityTest) return profile == EvaluationProfile.Adversarial;
        return EvaluationMix.PlanFor(profile).Shape is not { } shape || definition.Variants.Any(v => v.Shape == shape);
    }

    /// <summary>Negative controls: decoys every 1.5-3 minutes, with no incident behind them.</summary>
    private static List<(DistractorKind Kind, DateTime At)> PlanStandaloneDistractors(NegativeControl control, Random rng, DateTime start, DateTime end)
    {
        DistractorKind[] kinds = control switch
        {
            NegativeControl.Benign => [DistractorKind.BenignNoise],
            NegativeControl.Noisy => Enum.GetValues<DistractorKind>(),
            _ => [],
        };
        var plan = new List<(DistractorKind, DateTime)>();
        if (kinds.Length == 0) return plan;

        var i = 0;
        for (var at = start.AddSeconds(rng.Next(30, 90)); at < end.AddMinutes(-1); at = at.AddSeconds(rng.Next(90, 181)))
            plan.Add((kinds[i++ % kinds.Length], at));
        return plan;
    }

    private static DateTime TruncateToMinute(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
}
