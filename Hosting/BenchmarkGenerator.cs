using System.Text.Json;
using SampleLogGenerator.Configuration;
using SampleLogGenerator.Models;
using SampleLogGenerator.Output;

namespace SampleLogGenerator.Hosting;

public sealed record BenchmarkRequest(
    string Name = "benchmark",
    int Seed = 42,
    int IncidentsPerSplit = 10,
    int MinutesPerIncident = 4,
    int ControlMinutes = 30,
    int? MinUsers = null,
    int? MaxUsers = null);

public sealed record BenchmarkSplit(string Name, string Purpose, DatasetResult Dataset);

/// <summary>
/// A benchmark: one validated dataset per evaluation bucket, plus negative controls with no incident at all.
/// Scoring each split separately shows where an investigator breaks down (e.g. fine on direct incidents, fooled by decoys).
/// </summary>
public static class BenchmarkGenerator
{
    public static IReadOnlyList<BenchmarkSplit> Generate(BenchmarkRequest request, LogGeneratorOptions options, string datasetsRoot)
    {
        if (request.IncidentsPerSplit is < 1 or > 100) throw new ArgumentException("IncidentsPerSplit must be between 1 and 100.");
        if (request.MinutesPerIncident is < 3 or > 30) throw new ArgumentException("MinutesPerIncident must be between 3 and 30.");
        if (request.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || request.Name.Contains(".."))
            throw new ArgumentException("Name contains invalid characters.");

        var root = Path.GetFullPath(Path.Combine(datasetsRoot, request.Name));

        // One split per evaluation bucket: every scenario takes part, in whichever variants fit the bucket.
        var specs = new (string Name, string Purpose, DatasetRequest Request)[]
        {
            ("clean", "Level 1: direct variants, root cause leads straight to the impact, no decoys", Incidents(EvaluationProfile.Clean)),
            ("correlated", "Cause in one component or subset of hosts surfaces elsewhere, no decoys", Incidents(EvaluationProfile.Correlated)),
            ("cascade", "Level 3: three or more causal hops", Incidents(EvaluationProfile.Cascade)),
            ("noisy", "Level 2+: benign noise and misleading errors around every incident", Incidents(EvaluationProfile.Noisy)),
            ("competing-hypotheses", "Level 4: a competing change and a correlated-but-not-causal event with every incident",
                Incidents(EvaluationProfile.CompetingHypotheses)),
            ("adversarial", "Level 5: prompt-injection and hostile content on top of real outages, plus prompt-injection incidents",
                Incidents(EvaluationProfile.Adversarial) with { IncludePromptInjection = true }),
            ("control-healthy", "No incident: healthy traffic only", Control(NegativeControl.Healthy)),
            ("control-benign", "No incident: benign noise such as GC and brief self-healing slowdowns", Control(NegativeControl.Benign)),
            ("control-noisy", "No incident: every kind of decoy, but nothing is actually broken", Control(NegativeControl.Noisy)),
        };

        var splits = new List<BenchmarkSplit>();
        for (var i = 0; i < specs.Length; i++)
        {
            var (name, purpose, datasetRequest) = specs[i];
            var dataset = DatasetGenerator.Generate(datasetRequest with { Name = name, Seed = request.Seed + i }, options, root);
            splits.Add(new BenchmarkSplit(name, purpose, dataset));
        }

        File.WriteAllText(Path.Combine(root, "benchmark.json"), JsonSerializer.Serialize(new
        {
            request.Name,
            Generator = new { GeneratorInfo.Version, GeneratorInfo.Commit, GeneratorInfo.SchemaVersion },
            request.Seed,
            ValidationPassed = splits.All(s => s.Dataset.ValidationPassed),
            Splits = splits.Select(s => new
            {
                s.Name,
                s.Purpose,
                s.Dataset.Seed,
                s.Dataset.Control,
                Incidents = s.Dataset.Incidents.Count,
                Difficulties = s.Dataset.Incidents.GroupBy(x => x.Difficulty).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                StandaloneDistractors = s.Dataset.StandaloneDistractors.Count,
                s.Dataset.LogCount,
                s.Dataset.ValidationPassed,
            }),
        }, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }));

        return splits;

        DatasetRequest Incidents(EvaluationProfile profile) => new(
            DurationMinutes: request.IncidentsPerSplit * request.MinutesPerIncident,
            Incidents: request.IncidentsPerSplit,
            Mix: new() { [profile] = 1 },
            MinUsers: request.MinUsers,
            MaxUsers: request.MaxUsers);

        DatasetRequest Control(NegativeControl control) => new(
            DurationMinutes: request.ControlMinutes,
            Incidents: 0,
            Control: control,
            MinUsers: request.MinUsers,
            MaxUsers: request.MaxUsers);
    }
}
