using Microsoft.Extensions.Options;
using SampleLogGenerator.Configuration;
using SampleLogGenerator.Models;
using SampleLogGenerator.Output;
using SampleLogGenerator.Simulation;

namespace SampleLogGenerator.Hosting;

public sealed record GeneratorStatus(
    bool IsRunning,
    string Environment,
    int Seed,
    int ActiveUsers,
    int TargetUsers,
    string UserRange,
    bool AutoIncidents,
    long LogsWritten,
    long MetricsWritten,
    int PendingEvents,
    IncidentRecord? ActiveIncident,
    string LogsFile,
    string MetricsFile,
    string IncidentsFile);

/// <summary>
/// Owns the live <see cref="LogGenerator"/> and its output files. All access goes through a lock
/// because the background ticker and HTTP requests run on different threads.
/// </summary>
public sealed class SimulationHost : IDisposable
{
    private readonly Lock _gate = new();
    private readonly LogGeneratorOptions _options;
    private readonly LogBroadcaster _broadcaster;
    private readonly LogGenerator _generator;
    private readonly JsonlWriter _logs;
    private readonly JsonlWriter _metrics;
    private readonly JsonlWriter _incidents;
    private long _logsWritten;
    private long _metricsWritten;

    public SimulationHost(IOptions<LogGeneratorOptions> options, IHostEnvironment environment, LogBroadcaster broadcaster)
    {
        _options = options.Value;
        if (_options.MinConcurrentUsers < 1 || _options.MaxConcurrentUsers < _options.MinConcurrentUsers)
            throw new InvalidOperationException("LogGenerator: MinConcurrentUsers must be >= 1 and <= MaxConcurrentUsers.");
        _broadcaster = broadcaster;
        _generator = new LogGenerator(_options);

        var outputDirectory = Path.Combine(environment.ContentRootPath, _options.OutputDirectory);
        _logs = new JsonlWriter(Path.Combine(outputDirectory, "logs.jsonl"));
        _metrics = new JsonlWriter(Path.Combine(outputDirectory, "metrics.jsonl"));
        _incidents = new JsonlWriter(Path.Combine(outputDirectory, "incidents.jsonl"));
        IsRunning = _options.AutoStart;
    }

    public bool IsRunning { get; private set; }
    public string LogsPath => _logs.Path;
    public string MetricsPath => _metrics.Path;
    public string IncidentsPath => _incidents.Path;

    public void Tick(DateTime now)
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            Persist(_generator.Advance(now));
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;
            _generator.Resume(DateTime.UtcNow);
            IsRunning = true;
        }
    }

    public void Stop()
    {
        lock (_gate) IsRunning = false;
    }

    /// <exception cref="InvalidOperationException">Another incident is active or the generator is stopped.</exception>
    public IncidentRecord TriggerIncident(IncidentScenario scenario, TimeSpan? duration, int? distractors)
    {
        lock (_gate)
        {
            if (!IsRunning) throw new InvalidOperationException("The generator is stopped. POST /generator/start first.");
            return _generator.StartIncident(scenario, DateTime.UtcNow, duration, distractors);
        }
    }

    public IncidentRecord? ResolveIncident()
    {
        lock (_gate) return _generator.ResolveActiveIncident(DateTime.UtcNow);
    }

    /// <summary>Completed incidents from the ground-truth file plus the active one, newest first.</summary>
    public IReadOnlyList<IncidentRecord> Incidents()
    {
        var completed = JsonlReader.ReadAll<IncidentRecord>(IncidentsPath).ToList();
        lock (_gate)
        {
            if (_generator.ActiveIncident is { } active) completed.Add(active);
        }
        return [.. completed.OrderByDescending(i => i.StartedAt)];
    }

    public GeneratorStatus Status()
    {
        lock (_gate)
        {
            return new GeneratorStatus(IsRunning, _options.Environment, _generator.Seed, _generator.ActiveUsers, _generator.TargetUsers,
                $"{_options.MinConcurrentUsers}-{_options.MaxConcurrentUsers}", _options.AutoIncidents,
                _logsWritten, _metricsWritten, _generator.PendingEvents, _generator.ActiveIncident, LogsPath, MetricsPath, IncidentsPath);
        }
    }

    /// <summary>Records an in-flight incident as interrupted so the ground truth file stays complete.</summary>
    public void Shutdown()
    {
        lock (_gate) _incidents.WriteMany(_generator.Interrupt(DateTime.UtcNow));
    }

    private void Persist(GeneratorBatch batch)
    {
        if (batch.Logs.Count > 0)
        {
            _logs.WriteMany(batch.Logs);
            _logsWritten += batch.Logs.Count;
            _broadcaster.Publish(batch.Logs);
            if (_options.EchoToConsole) ConsoleEcho.Write(batch.Logs);
        }

        if (batch.Metrics.Count > 0)
        {
            _metrics.WriteMany(batch.Metrics);
            _metricsWritten += batch.Metrics.Count;
        }

        if (batch.CompletedIncidents.Count > 0)
            _incidents.WriteMany(batch.CompletedIncidents);
    }

    public void Dispose()
    {
        _logs.Dispose();
        _metrics.Dispose();
        _incidents.Dispose();
    }
}
