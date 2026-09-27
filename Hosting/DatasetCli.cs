using SampleLogGenerator.Configuration;
using SampleLogGenerator.Models;

namespace SampleLogGenerator.Hosting;

/// <summary>
/// dotnet run -- dataset [--minutes 60] [--incidents 5] [--rate 2] [--seed 42] [--name demo]
///                       [--scenarios PaymentGatewayTimeout,NetworkTimeout] [--prompt-injection]
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
            Console.Error.WriteLine("Usage: dotnet run -- dataset [--minutes 60] [--incidents 5] [--rate 2] [--seed 42] [--name demo] [--scenarios A,B] [--prompt-injection]");
            return 1;
        }

        var result = DatasetGenerator.Generate(request, options, Path.GetFullPath(options.DatasetsDirectory));
        Console.WriteLine($"Dataset '{result.Name}' written to {result.Directory}");
        Console.WriteLine($"  Window : {result.StartTime:u} -> {result.EndTime:u}");
        Console.WriteLine($"  Seed   : {result.Seed}");
        Console.WriteLine($"  Logs   : {result.LogCount:N0}");
        Console.WriteLine($"  Metrics: {result.MetricCount:N0}");
        Console.WriteLine($"  Incidents ({result.Incidents.Count}):");
        foreach (var i in result.Incidents)
            Console.WriteLine($"    {i.IncidentId}  {i.Scenario,-32} {i.StartedAt:HH:mm:ss}-{i.EndedAt:HH:mm:ss}  affected requests: {i.AffectedRequestCount}");
        return 0;
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
                "--rate" => request with { OrdersPerSecond = double.Parse(args[++i]) },
                "--seed" => request with { Seed = int.Parse(args[++i]) },
                "--name" => request with { Name = args[++i] },
                "--scenarios" => request with
                {
                    Scenarios = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => Enum.Parse<IncidentScenario>(s, ignoreCase: true))]
                },
                "--prompt-injection" => request with { IncludePromptInjection = true },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'"),
            };
        }
        return request;
    }
}
