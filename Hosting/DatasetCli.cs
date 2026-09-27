using SampleLogGenerator.Configuration;
using SampleLogGenerator.Models;
using SampleLogGenerator.Validation;

namespace SampleLogGenerator.Hosting;

/// <summary>
/// Offline commands:
/// <code>
/// dotnet run -- dataset   [--minutes 60] [--incidents 5] [--users 30-100] [--seed 42] [--name demo]
///                         [--scenarios A,B] [--prompt-injection] [--distractors 2] [--distractor-kinds K1,K2]
///                         [--control healthy|benign|noisy] [--mix evaluation | --mix clean=20,cascade=25,...]
/// dotnet run -- validate  datasets/demo
/// dotnet run -- benchmark [--name bench] [--seed 42] [--incidents-per-split 10] [--users 30-100]
/// </code>
/// Exit code 0 = success, 1 = bad arguments, 2 = validation failed.
/// </summary>
public static class DatasetCli
{
    public static readonly string[] Commands = ["dataset", "validate", "benchmark"];

    private const string Usage =
        "Usage:\n" +
        "  dotnet run -- dataset [--minutes 60] [--incidents 5] [--users 30-100] [--seed 42] [--name demo] [--scenarios A,B]\n" +
        "                        [--prompt-injection] [--distractors 2] [--distractor-kinds CompetingHypothesis,SecurityNoise] [--control healthy|benign|noisy]\n" +
        "                        [--mix evaluation | --mix clean=20,correlated=20,cascade=25,noisy=20,competinghypotheses=10,adversarial=5]\n" +
        "  dotnet run -- validate <dataset directory>\n" +
        "  dotnet run -- benchmark [--name benchmark] [--seed 42] [--incidents-per-split 10] [--users 30-100]";

    public static int Run(string command, string[] args, LogGeneratorOptions options)
    {
        try
        {
            return command switch
            {
                "dataset" => RunDataset(ParseDataset(args), options),
                "validate" => RunValidate(args.Length == 1 ? args[0] : throw new ArgumentException("validate takes one dataset directory")),
                "benchmark" => RunBenchmark(ParseBenchmark(args), options),
                _ => throw new ArgumentException($"Unknown command '{command}'"),
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine($"Invalid arguments: {ex.Message}");
            Console.Error.WriteLine(Usage);
            return 1;
        }
    }

    private static int RunDataset(DatasetRequest request, LogGeneratorOptions options)
    {
        var result = DatasetGenerator.Generate(request, options, Path.GetFullPath(options.DatasetsDirectory));
        Console.WriteLine($"Dataset '{result.Name}' written to {result.Directory}");
        Console.WriteLine($"  Generator: v{GeneratorInfo.Version} ({GeneratorInfo.Commit}), schema {GeneratorInfo.SchemaVersion}");
        Console.WriteLine($"  Window   : {result.StartTime:u} -> {result.EndTime:u}");
        Console.WriteLine($"  Seed     : {result.Seed}");
        Console.WriteLine($"  Users    : {result.Users} concurrent");
        if (result.Control != NegativeControl.None)
            Console.WriteLine($"  Control  : {result.Control} ({result.StandaloneDistractors.Count} standalone distractors, no incidents)");
        Console.WriteLine($"  Logs     : {result.LogCount:N0}");
        Console.WriteLine($"  Metrics  : {result.MetricCount:N0}");
        Console.WriteLine($"  Incidents ({result.Incidents.Count}):");
        if (result.Incidents.Count <= 30)
        {
            foreach (var i in result.Incidents)
            {
                var kinds = i.Distractors.Count == 0 ? "" : " [" + string.Join(", ", i.Distractors.Select(d => d.Kind)) + "]";
                Console.WriteLine($"    {i.IncidentId}  {i.Scenario,-32} {i.Variant,-40} L{(int)i.Difficulty} {i.StartedAt:HH:mm:ss}-{i.EndedAt:HH:mm:ss}  affected requests: {i.AffectedRequestCount}{kinds}");
            }
        }
        else
        {
            foreach (var g in result.Incidents.GroupBy(i => i.Scenario).OrderBy(g => g.Key))
                Console.WriteLine($"    {g.Key,-32} {g.Count(),4}  ({g.Select(i => i.Variant).Distinct().Count()} variants)");
            Console.WriteLine("    Difficulty: " + string.Join(", ", result.Incidents.GroupBy(i => i.Difficulty).OrderBy(g => g.Key).Select(g => $"L{(int)g.Key} {g.Key} x{g.Count()}")));
            if (result.Incidents.Any(i => i.Profile is not null))
                Console.WriteLine("    Profile   : " + string.Join(", ", result.Incidents.GroupBy(i => i.Profile).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}")));
        }
        Console.WriteLine($"  Investigator input : {Path.Combine(result.Directory, DatasetLayout.Input)}");
        Console.WriteLine($"  Ground truth       : {Path.Combine(result.Directory, DatasetLayout.GroundTruth)} (evaluator only)");
        Console.WriteLine($"  Validation: {(result.ValidationPassed ? "PASSED" : $"FAILED ({result.FailedChecks} checks)")} - see ground-truth/validation.json");
        return result.ValidationPassed ? 0 : 2;
    }

    private static int RunValidate(string directory)
    {
        var path = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(DatasetLayout.InputDir(path), "logs.jsonl"))) throw new ArgumentException($"No logs.jsonl in {path}");

        var report = DatasetValidator.Validate(path);
        DatasetValidator.Write(report, path);
        Console.WriteLine($"Dataset {report.Dataset}: {report.LogCount:N0} logs, {report.IncidentCount} incidents");
        foreach (var check in report.DatasetChecks)
            Console.WriteLine($"  [{(check.Passed ? "ok" : "FAIL")}] {check.Name}: {check.Detail}");

        foreach (var group in report.Incidents.SelectMany(i => i.Checks).GroupBy(c => c.Name))
            Console.WriteLine($"  [{(group.All(c => c.Passed) ? "ok" : "FAIL")}] {group.Key}: {group.Count(c => c.Passed)}/{group.Count()} incidents");

        foreach (var incident in report.Incidents.Where(i => !i.Passed))
            foreach (var check in incident.Checks.Where(c => !c.Passed))
                Console.WriteLine($"    {incident.IncidentId} {incident.Scenario} {check.Name}: {check.Detail}");

        Console.WriteLine(report.Passed ? "PASSED" : $"FAILED ({report.FailedChecks} checks)");
        return report.Passed ? 0 : 2;
    }

    private static int RunBenchmark(BenchmarkRequest request, LogGeneratorOptions options)
    {
        var splits = BenchmarkGenerator.Generate(request, options, Path.GetFullPath(options.DatasetsDirectory));
        Console.WriteLine($"Benchmark '{request.Name}' (generator v{GeneratorInfo.Version}, {GeneratorInfo.Commit}):");
        foreach (var s in splits)
        {
            var what = s.Dataset.Control == NegativeControl.None
                ? $"{s.Dataset.Incidents.Count} incidents ({string.Join(", ", s.Dataset.Incidents.GroupBy(i => i.Difficulty).Select(g => $"L{(int)g.Key} x{g.Count()}"))})"
                : $"no incident, {s.Dataset.StandaloneDistractors.Count} standalone distractors";
            Console.WriteLine($"  {s.Name,-22} {what,-44} {s.Dataset.LogCount,7:N0} logs  validation {(s.Dataset.ValidationPassed ? "PASSED" : "FAILED")}");
        }
        var passed = splits.All(s => s.Dataset.ValidationPassed);
        Console.WriteLine(passed ? "All splits PASSED validation" : "Some splits FAILED validation - see each split's validation.json");
        return passed ? 0 : 2;
    }

    /// <summary>"--users 30-100" for a range, or "--users 50" for a fixed number.</summary>
    private static (int Min, int Max) ParseUsers(string value)
    {
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        var min = int.Parse(parts[0]);
        return (min, parts.Length == 2 ? int.Parse(parts[1]) : min);
    }

    private static List<T> ParseEnums<T>(string value) where T : struct, Enum =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => Enum.Parse<T>(s, ignoreCase: true))];

    private static DatasetRequest ParseDataset(string[] args)
    {
        var request = new DatasetRequest();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--minutes": request = request with { DurationMinutes = int.Parse(args[++i]) }; break;
                case "--incidents": request = request with { Incidents = int.Parse(args[++i]) }; break;
                case "--users":
                    var (min, max) = ParseUsers(args[++i]);
                    request = request with { MinUsers = min, MaxUsers = max };
                    break;
                case "--seed": request = request with { Seed = int.Parse(args[++i]) }; break;
                case "--name": request = request with { Name = args[++i] }; break;
                case "--scenarios": request = request with { Scenarios = ParseEnums<IncidentScenario>(args[++i]) }; break;
                case "--prompt-injection": request = request with { IncludePromptInjection = true }; break;
                case "--distractors": request = request with { Distractors = int.Parse(args[++i]) }; break;
                case "--distractor-kinds": request = request with { DistractorKinds = ParseEnums<DistractorKind>(args[++i]) }; break;
                case "--control": request = request with { Control = Enum.Parse<NegativeControl>(args[++i], ignoreCase: true) }; break;
                case "--mix": request = request with { Mix = ParseMix(args[++i]) }; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'");
            }
        }
        return request;
    }

    /// <summary>"evaluation" for the recommended mix, or "clean=20,cascade=25,..." (weights, need not add up to 100).</summary>
    private static Dictionary<EvaluationProfile, double> ParseMix(string value)
    {
        if (value.Equals("evaluation", StringComparison.OrdinalIgnoreCase)) return EvaluationMix.Default;
        var mix = new Dictionary<EvaluationProfile, double>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) throw new FormatException($"Expected profile=weight, got '{part}'");
            mix[Enum.Parse<EvaluationProfile>(pair[0], ignoreCase: true)] = double.Parse(pair[1]);
        }
        return mix;
    }

    private static BenchmarkRequest ParseBenchmark(string[] args)
    {
        var request = new BenchmarkRequest();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name": request = request with { Name = args[++i] }; break;
                case "--seed": request = request with { Seed = int.Parse(args[++i]) }; break;
                case "--incidents-per-split": request = request with { IncidentsPerSplit = int.Parse(args[++i]) }; break;
                case "--minutes-per-incident": request = request with { MinutesPerIncident = int.Parse(args[++i]) }; break;
                case "--control-minutes": request = request with { ControlMinutes = int.Parse(args[++i]) }; break;
                case "--users":
                    var (min, max) = ParseUsers(args[++i]);
                    request = request with { MinUsers = min, MaxUsers = max };
                    break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'");
            }
        }
        return request;
    }
}
