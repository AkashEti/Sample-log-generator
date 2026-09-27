using System.Reflection;

namespace SampleLogGenerator.Hosting;

/// <summary>Identifies the exact generator build that produced a dataset, so evaluation results can be traced back to it.</summary>
public static class GeneratorInfo
{
    /// <summary>Version of the dataset file formats (logs, metrics, incidents, distractors). Bump when a field changes meaning.</summary>
    public const int SchemaVersion = 4;

    private static readonly string Informational =
        typeof(GeneratorInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Semantic version of the generator (from the project's &lt;Version&gt;).</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>Git commit the generator was built from ("-dirty" when there were uncommitted changes), or "unknown".</summary>
    public static string Commit => Informational.Contains('+') ? Informational.Split('+', 2)[1] : "unknown";
}
