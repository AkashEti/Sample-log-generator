using SampleLogGenerator.Models;
using SampleLogGenerator.Simulation.Scenarios;

namespace SampleLogGenerator.Simulation;

/// <summary>
/// Turns emitted log lines into per-service metrics. Error rate and latency are derived from the logs
/// themselves, so metrics and logs always agree; gauges (CPU, memory, pool usage...) use a noisy
/// baseline that the active incident scenario can override.
/// </summary>
internal sealed class MetricsAggregator(string environment)
{
    private sealed class Window
    {
        public int Events;
        public int Errors;
        public readonly List<int> Durations = [];
    }

    private static readonly Dictionary<string, (double Min, double Max)> MemoryBaseline = new()
    {
        [Services.Order] = (420, 520),
        [Services.Auth] = (260, 320),
        [Services.Inventory] = (900, 1300),
        [Services.Payment] = (480, 600),
        [Services.Notification] = (200, 260),
        [Services.Catalog] = (600, 780),
        [Services.Cart] = (300, 380),
    };

    private static readonly Dictionary<string, (string Metric, string Unit, double Min, double Max)[]> ServiceGauges = new()
    {
        [Services.Order] =
        [
            ("upstream_payment_latency_p95_ms", "ms", 180, 520),
            ("threadpool_queue_length", "count", 0, 4),
        ],
        [Services.Auth] = [("cache_hit_ratio_percent", "percent", 97, 99)],
        [Services.Inventory] =
        [
            ("healthy_instances", "count", 3, 3),
            ("db_lock_wait_ms", "ms", 0, 5),
            ("threadpool_queue_length", "count", 0, 3),
        ],
        [Services.Notification] = [("queue_depth", "count", 0, 25)],
        [Services.Payment] =
        [
            ("db_connections_active", "count", 8, 22),
            ("db_connection_wait_ms", "ms", 0, 4),
            ("payment_gateway_latency_p95_ms", "ms", 320, 720),
        ],
    };

    private readonly Dictionary<string, Window> _windows = Services.All.ToDictionary(s => s, _ => new Window());

    public void Observe(LogEntry entry)
    {
        var window = _windows[entry.Service];
        window.Events++;
        if (Levels.Rank(entry.Level) >= Levels.Rank(Levels.Error)) window.Errors++;
        if (entry.DurationMs is { } ms) window.Durations.Add(ms);
    }

    public IEnumerable<MetricSample> Snapshot(DateTime at, ActiveIncident? incident, Random rng)
    {
        var samples = new List<MetricSample>();
        var active = incident is not null && incident.IsActiveAt(at) ? incident : null;

        foreach (var service in Services.All)
        {
            var w = _windows[service];
            double Value(string metric, double baseline) =>
                active?.Scenario.Gauge(service, metric, active, at, rng) ?? baseline;
            void Add(string metric, double value, string unit) => samples.Add(new MetricSample
            {
                Timestamp = at, Service = service, Metric = metric, Value = Math.Round(value, 2), Unit = unit, Environment = environment,
            });

            Add("event_count", w.Events, "count");
            Add("error_count", w.Errors, "count");
            Add("error_rate_percent", w.Events == 0 ? 0 : 100.0 * w.Errors / w.Events, "percent");
            if (w.Durations.Count > 0) Add("latency_p95_ms", Percentile95(w.Durations), "ms");
            Add("cpu_percent", Value("cpu_percent", rng.Between(12.0, 38.0)), "percent");
            var (memMin, memMax) = MemoryBaseline[service];
            Add("memory_mb", Value("memory_mb", rng.Between(memMin, memMax)), "MB");

            if (ServiceGauges.TryGetValue(service, out var gauges))
                foreach (var (metric, unit, min, max) in gauges)
                    Add(metric, Value(metric, Math.Round(rng.Between(min, max))), unit);

            w.Events = 0;
            w.Errors = 0;
            w.Durations.Clear();
        }

        return samples;
    }

    private static double Percentile95(List<int> values)
    {
        values.Sort();
        return values[(int)Math.Ceiling(values.Count * 0.95) - 1];
    }
}
