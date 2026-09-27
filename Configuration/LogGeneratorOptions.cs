using SampleLogGenerator.Models;

namespace SampleLogGenerator.Configuration;

/// <summary>Bound from the "LogGenerator" section of appsettings.json.</summary>
public sealed class LogGeneratorOptions
{
    public const string SectionName = "LogGenerator";

    /// <summary>Start emitting live logs as soon as the app starts.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Folder for the live logs.jsonl, metrics.jsonl and incidents.jsonl files.</summary>
    public string OutputDirectory { get; set; } = "logs";

    /// <summary>Folder where offline datasets are generated.</summary>
    public string DatasetsDirectory { get; set; } = "datasets";

    public string Environment { get; set; } = "Production";

    /// <summary>
    /// Range for the number of concurrent visitors. A new target within the range is picked every 2-6 minutes;
    /// each visitor browses, searches, fills a cart and sometimes checks out.
    /// </summary>
    public int MinConcurrentUsers { get; set; } = 30;

    public int MaxConcurrentUsers { get; set; } = 100;

    /// <summary>How often the live generator flushes due events.</summary>
    public int TickMilliseconds { get; set; } = 250;

    public int MetricsIntervalSeconds { get; set; } = 10;

    /// <summary>Randomly inject incidents during live generation.</summary>
    public bool AutoIncidents { get; set; } = true;

    public int MinSecondsBetweenIncidents { get; set; } = 120;
    public int MaxSecondsBetweenIncidents { get; set; } = 300;
    public int MinIncidentDurationSeconds { get; set; } = 60;
    public int MaxIncidentDurationSeconds { get; set; } = 180;

    /// <summary>Scenarios used for automatic injection. Empty means all outage scenarios.</summary>
    public List<IncidentScenario> EnabledScenarios { get; set; } = [];

    /// <summary>
    /// Unrelated but plausible events (deploys, GC pauses, flag changes...) emitted around each incident.
    /// 1-2 raise the difficulty to CompetingHypotheses, 3+ to NoisyMisleading.
    /// </summary>
    public int DistractorsPerIncident { get; set; }

    /// <summary>Also inject <see cref="IncidentScenario.PromptInjectionAttempt"/> automatically.</summary>
    public bool IncludePromptInjection { get; set; }

    /// <summary>Fixed seed for reproducible output. Null picks a random seed.</summary>
    public int? Seed { get; set; }

    /// <summary>Print generated log lines to the console.</summary>
    public bool EchoToConsole { get; set; } = true;

    public LogGeneratorOptions Clone()
    {
        var clone = (LogGeneratorOptions)MemberwiseClone();
        clone.EnabledScenarios = [.. EnabledScenarios];
        return clone;
    }
}
