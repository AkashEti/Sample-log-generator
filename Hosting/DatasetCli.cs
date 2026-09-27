using SampleLogGenerator.Configuration;
using SampleLogGenerator.Models;

namespace SampleLogGenerator.Hosting;

/// <summary>
/// dotnet run -- dataset [--minutes 60] [--incidents 5] [--users 30-100] [--seed 42] [--name demo]
///                       [--scenarios PaymentGatewayTimeout,NetworkTimeout] [--prompt-injection] [--distractors 2]
/// </summary>
public static class DatasetCli
{
    public static int Run(string[] args, LogGeneratorOptions options)
    {
        DatasetRequest request;
        try
        {
            request = Parse(args);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine($"Invalid arguments: {ex.Message}");
            Console.Error.WriteLine("Usage: dotnet run -- dataset [--minutes 60] [--incidents 5] [--users 30-100] [--seed 42] [--name demo] [--scenarios A,B] [--prompt-injection] [--distractors 2]");
            return 1;
        }

        var result = DatasetGenerator.Generate(request, options, Path.GetFullPath(options.DatasetsDirectory));
        Console.WriteLine($"Dataset '{result.Name}' written to {result.Directory}");
        Console.WriteLine($"  Window : {result.StartTime:u} -> {result.EndTime:u}");
        Console.WriteLine($"  Seed   : {result.Seed}");
        Console.WriteLine($"  Users  : {result.Users} concurrent");
        Console.WriteLine($"  Logs   : {result.LogCount:N0}");
        Console.WriteLine($"  Metrics: {result.MetricCount:N0}");
        Console.WriteLine($"  Incidents ({result.Incidents.Count}):");
        foreach (var i in result.Incidents)
            Console.WriteLine($"    {i.IncidentId}  {i.Scenario,-32} L{(int)i.Difficulty} {i.StartedAt:HH:mm:ss}-{i.EndedAt:HH:mm:ss}  affected requests: {i.AffectedRequestCount}, distractors: {i.Distractors.Count}");
        return 0;
    }

    /// <summary>"--users 30-100" for a range, or "--users 50" for a fixed number.</summary>
    private static DatasetRequest ParseUsers(DatasetRequest request, string value)
    {
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        var min = int.Parse(parts[0]);
        return request with { MinUsers = min, MaxUsers = parts.Length == 2 ? int.Parse(parts[1]) : min };
    }

    private static DatasetRequest Parse(string[] args)
    {
        var request = new DatasetRequest();
        for (var i = 0; i < args.Length; i++)
        {
            request = args[i] switch
            {
                "--minutes" => request with { DurationMinutes = int.Parse(args[++i]) },
                "--incidents" => request with { Incidents = int.Parse(args[++i]) },
                "--users" => ParseUsers(request, args[++i]),
                "--seed" => request with { Seed = int.Parse(args[++i]) },
                "--name" => request with { Name = args[++i] },
                "--scenarios" => request with
                {
                    Scenarios = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => Enum.Parse<IncidentScenario>(s, ignoreCase: true))]
                },
                "--prompt-injection" => request with { IncludePromptInjection = true },
                "--distractors" => request with { Distractors = int.Parse(args[++i]) },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'"),
            };
        }
        return request;
    }
}
