using SampleLogGenerator.Configuration;
using SampleLogGenerator.Models;
using SampleLogGenerator.Simulation.Scenarios;

namespace SampleLogGenerator.Simulation;

/// <summary>Everything the generator produced during one <see cref="LogGenerator.Advance"/> call.</summary>
public sealed class GeneratorBatch
{
    public List<LogEntry> Logs { get; } = [];
    public List<MetricSample> Metrics { get; } = [];
    public List<IncidentRecord> CompletedIncidents { get; } = [];
}

/// <summary>
/// Simulates concurrent user traffic across the e-commerce services and injects known incident scenarios.
/// It is clock-driven: call <see cref="Advance"/> with the current time (live mode) or with a simulated
/// time (dataset mode) and it returns the log lines and metrics that became due, in timestamp order.
/// Not thread-safe; callers synchronise access.
/// </summary>
public sealed class LogGenerator
{
    private const double NoiseEventsPerSecond = 1.0 / 15;

    private readonly LogGeneratorOptions _options;
    private readonly IReadOnlyDictionary<IncidentScenario, IncidentScenarioBase> _scenarios = ScenarioCatalog.All;
    private readonly PriorityQueue<LogEntry, (DateTime Timestamp, long Sequence)> _pending = new();
    private readonly List<IncidentRecord> _completed = [];
    private readonly MetricsAggregator _metrics;
    private readonly UserTraffic _traffic;
    private long _sequence;
    private long _nextOrderNumber;
    private bool _started;
    private DateTime _now;
    private DateTime _nextNoiseAt;
    private DateTime _nextIncidentAt;
    private DateTime _nextMetricsAt;
    private ActiveIncident? _active;

    public LogGenerator(LogGeneratorOptions options, int? seed = null)
    {
        _options = options;
        Seed = seed ?? options.Seed ?? Random.Shared.Next();
        Rng = new Random(Seed);
        _metrics = new MetricsAggregator(options.Environment);
        _nextOrderNumber = Rng.Next(100_000, 200_000);
        _traffic = new UserTraffic(this, options);
    }

    public int Seed { get; }
    public int ActiveUsers => _traffic.ActiveUsers;
    public int TargetUsers => _traffic.TargetUsers;
    public IncidentRecord? ActiveIncident => _active?.Record;
    public int PendingEvents => _pending.Count;
    internal Random Rng { get; }

    public GeneratorBatch Advance(DateTime now)
    {
        if (!_started) Initialize(now);
        if (now < _now) now = _now;

        if (_active is not null && now >= _active.EndsAt)
            EndIncident(_active.EndsAt, IncidentStatus.Resolved, emitRecovery: true);

        if (_active is null && _options.AutoIncidents && now >= _nextIncidentAt)
        {
            var scenario = PickAutoScenario();
            if (scenario is null) _nextIncidentAt = now + RandomGap();
            else StartIncident(scenario.Value, _nextIncidentAt);
        }

        _traffic.Advance(now);

        while (_nextNoiseAt <= now)
        {
            EmitNoise(_nextNoiseAt);
            _nextNoiseAt += Rng.Exponential(NoiseEventsPerSecond);
        }

        if (_active is not null)
        {
            while (_active.NextBackgroundAt <= now && _active.NextBackgroundAt < _active.EndsAt)
            {
                _active.Scenario.OnBackground(this, _active, _active.NextBackgroundAt);
                _active.BackgroundTicks++;
                _active.NextBackgroundAt += _active.Scenario.BackgroundInterval;
            }
        }

        var batch = new GeneratorBatch();
        while (_pending.TryPeek(out var entry, out var priority) && priority.Timestamp <= now)
        {
            _pending.Dequeue();
            _metrics.Observe(entry);
            batch.Logs.Add(entry);
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.MetricsIntervalSeconds));
        while (_nextMetricsAt <= now)
        {
            batch.Metrics.AddRange(_metrics.Snapshot(_nextMetricsAt, _active, Rng));
            batch.Metrics.Add(new MetricSample
            {
                Timestamp = _nextMetricsAt, Service = "Platform", Metric = "active_users", Value = _traffic.ActiveUsers, Unit = "count",
                Environment = _options.Environment,
            });
            _nextMetricsAt += interval;
        }

        batch.CompletedIncidents.AddRange(_completed);
        _completed.Clear();
        _now = now;
        return batch;
    }

    /// <summary>Starts an incident now. Only one incident is active at a time.</summary>
    /// <param name="distractors">Unrelated decoy events to emit; defaults to <see cref="LogGeneratorOptions.DistractorsPerIncident"/>.</param>
    public IncidentRecord StartIncident(IncidentScenario scenario, DateTime at, TimeSpan? duration = null, int? distractors = null)
    {
        if (_active is not null)
            throw new InvalidOperationException($"Incident {_active.Record.IncidentId} ({_active.Record.Scenario}) is still active.");
        if (!_started) Initialize(at);

        var definition = _scenarios[scenario];
        var incidentDuration = duration ?? RandomDuration();
        var decoys = PickDistractors(definition, distractors ?? _options.DistractorsPerIncident);
        var record = new IncidentRecord
        {
            IncidentId = $"INC-{at:yyyyMMdd-HHmmss}-{Rng.Hex(4).ToUpperInvariant()}",
            Scenario = scenario,
            Title = definition.Title,
            Difficulty = DifficultyFor(definition.Difficulty, decoys.Count),
            StartedAt = TruncateToMilliseconds(at),
            RootCauseService = definition.RootCauseService,
            RootCause = definition.RootCause,
            AffectedServices = definition.AffectedServices,
            ExpectedEvidence = definition.ExpectedEvidence,
            CausalChain = definition.CausalChain,
            Remediation = definition.Remediation,
            IsSecurityTest = definition.IsSecurityTest,
            Distractors = [.. decoys.Select(d => d.ToHint())],
        };

        _active = new ActiveIncident
        {
            Record = record,
            Scenario = definition,
            StartedAt = at,
            EndsAt = at + incidentDuration,
            NextBackgroundAt = at + definition.BackgroundInterval,
        };
        definition.OnStart(this, _active, at);

        // Decoys land in the early part of the incident, when an investigator is forming hypotheses.
        var window = Math.Max(1, Math.Min(90, incidentDuration.TotalSeconds * 0.4));
        foreach (var decoy in decoys)
            decoy.Emit(this, at.AddSeconds(Rng.Between(0.0, window)));

        return record;
    }

    /// <summary>Random decoys, never one that shares an event id with the real evidence.</summary>
    private List<Distractor> PickDistractors(IncidentScenarioBase definition, int count)
    {
        if (count <= 0) return [];
        var evidenceIds = definition.ExpectedEvidence.Concat(definition.CausalChain).Select(e => e.EventId).ToHashSet();
        return [.. Distractor.All.Where(d => !evidenceIds.Contains(d.EventId)).OrderBy(_ => Rng.Next()).Take(count)];
    }

    private static IncidentDifficulty DifficultyFor(IncidentDifficulty inherent, int distractorCount)
    {
        var withDistractors = distractorCount switch
        {
            0 => inherent,
            < 3 => IncidentDifficulty.CompetingHypotheses,
            _ => IncidentDifficulty.NoisyMisleading,
        };
        return (IncidentDifficulty)Math.Max((int)inherent, (int)withDistractors);
    }

    /// <summary>Ends the active incident at the given time (recovery logs follow on the next Advance).</summary>
    public IncidentRecord? ResolveActiveIncident(DateTime at)
    {
        if (_active is null) return null;
        _active.EndsAt = at < _active.StartedAt ? _active.StartedAt : at;
        return _active.Record;
    }

    /// <summary>Resets the schedule after a pause so the gap is not back-filled with a burst of traffic.</summary>
    public void Resume(DateTime now)
    {
        if (!_started) return;
        _now = now;
        _traffic.Resume(now);
        if (_nextNoiseAt < now) _nextNoiseAt = now;
        if (_nextMetricsAt < now) _nextMetricsAt = AlignUp(now);
        if (_nextIncidentAt < now) _nextIncidentAt = now + RandomGap();
        if (_active is not null && _active.NextBackgroundAt < now) _active.NextBackgroundAt = now;
    }

    /// <summary>Marks the active incident as interrupted (app shutting down) without emitting recovery logs.</summary>
    public IReadOnlyList<IncidentRecord> Interrupt(DateTime at)
    {
        if (_active is not null) EndIncident(at, IncidentStatus.Interrupted, emitRecovery: false);
        var completed = _completed.ToList();
        _completed.Clear();
        return completed;
    }

    /// <summary>Dataset mode: closes the active incident and flushes every scheduled log line.</summary>
    public GeneratorBatch Drain(DateTime end)
    {
        if (_active is not null)
            EndIncident(_active.EndsAt < end ? _active.EndsAt : end, IncidentStatus.Resolved, emitRecovery: true);

        var batch = new GeneratorBatch();
        while (_pending.TryDequeue(out var entry, out _))
            batch.Logs.Add(entry);
        batch.CompletedIncidents.AddRange(_completed);
        _completed.Clear();
        return batch;
    }

    internal void Emit(DateTime timestamp, string level, string service, int eventId, string message,
        RequestContext? request = null, string? exception = null, int? durationMs = null, string? host = null)
    {
        var entry = new LogEntry
        {
            Timestamp = TruncateToMilliseconds(timestamp),
            Level = level,
            Service = service,
            Host = host ?? request?.HostFor(service, Rng) ?? Rng.Pick(Services.HostsFor(service)),
            Environment = _options.Environment,
            EventId = eventId,
            Message = message,
            CorrelationId = request?.CorrelationId,
            RequestId = request?.RequestIdFor(service, Rng),
            SessionId = request?.SessionId,
            UserId = request?.UserId,
            OrderId = request is OrderContext { IsCreated: true } order ? order.OrderId : null,
            DurationMs = durationMs,
            Exception = exception,
        };
        _pending.Enqueue(entry, (entry.Timestamp, _sequence++));
        // Compare with the truncated start too: an event logged at the very start instant must count as observed.
        if (_active is not null && entry.Timestamp >= TruncateToMilliseconds(_active.StartedAt)) _active.Observe(service, eventId);
    }

    internal static DateTime TruncateToMilliseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private void Initialize(DateTime now)
    {
        _started = true;
        _now = now;
        _traffic.Start(now);
        _nextNoiseAt = now + Rng.Exponential(NoiseEventsPerSecond);
        _nextIncidentAt = now + RandomGap();
        _nextMetricsAt = AlignUp(now);
    }

    /// <summary>A user checks out their cart. During an incident, the active scenario decides how the checkout goes.</summary>
    internal void Checkout(DateTime at, string sessionId, string customerId, IReadOnlyList<(Product Product, int Quantity)> cart)
    {
        var order = new OrderContext
        {
            OrderId = $"ORD-{_nextOrderNumber++}",
            CorrelationId = Rng.NextGuid().ToString(),
            SessionId = sessionId,
            UserId = customerId,
            CustomerId = customerId,
            PaymentMethod = Rng.Pick(PaymentMethods),
            Sku = cart[0].Product.Sku,
            ItemCount = cart.Sum(line => line.Quantity),
            Amount = cart.Sum(line => line.Product.Price * line.Quantity),
        };

        var flow = new Flow(this, order, at);
        if (_active is not null && _active.IsActiveAt(at))
            _active.Scenario.RunOrder(flow, _active);
        else
            CommonFlows.Normal(flow);
    }

    private static readonly string[] PaymentMethods = ["card", "card", "card", "paypal", "apple-pay"];

    /// <summary>Background events unrelated to checkouts, including harmless warnings (red herrings).</summary>
    private void EmitNoise(DateTime at)
    {
        var slowQueryMs = Rng.Between(900, 2400);
        var (level, service, eventId, message, durationMs) = Rng.Next(6) switch
        {
            0 => (Levels.Information, Services.Inventory, EventIds.StockCacheRefreshed,
                $"Stock cache refreshed: {Rng.Between(45000, 52000)} SKUs in {Rng.Between(300, 1200)} ms", (int?)null),
            1 => (Levels.Warning, Services.Payment, EventIds.SlowQuery,
                $"Slow query on payments-db: SELECT * FROM payment_transactions WHERE created_at > @since took {slowQueryMs} ms", slowQueryMs),
            2 => (Levels.Information, Services.Notification, EventIds.SmtpConnectionRecycled,
                $"SMTP connection pool recycled ({Rng.Between(2, 8)} connections)", null),
            3 => (Levels.Information, Services.Auth, EventIds.JwksRefreshed,
                $"JWKS signing keys refreshed ({Rng.Between(2, 3)} keys)", null),
            _ => (Levels.Information, Services.Order, EventIds.AbandonedCartCleanup,
                $"Abandoned cart cleanup removed {Rng.Between(0, 40)} carts", null),
        };

        if (_active is not null && _active.IsActiveAt(at) && _active.Scenario.SuppressesNoise(eventId)) return;
        Emit(at, level, service, eventId, message, durationMs: durationMs);
    }

    private IncidentScenario? PickAutoScenario()
    {
        var candidates = _options.EnabledScenarios.Count > 0
            ? _options.EnabledScenarios.Distinct().ToList()
            : _scenarios.Values.Where(s => !s.IsSecurityTest).Select(s => s.Scenario).ToList();
        if (_options.IncludePromptInjection && !candidates.Contains(IncidentScenario.PromptInjectionAttempt))
            candidates.Add(IncidentScenario.PromptInjectionAttempt);
        return candidates.Count == 0 ? null : Rng.Pick(candidates);
    }

    private void EndIncident(DateTime at, IncidentStatus status, bool emitRecovery)
    {
        var incident = _active!;
        if (emitRecovery) incident.Scenario.OnEnd(this, incident, at);
        incident.Complete(at, status);
        _completed.Add(incident.Record);
        _active = null;
        _nextIncidentAt = at + RandomGap();
    }

    private TimeSpan RandomDuration() => TimeSpan.FromSeconds(
        Rng.Between(_options.MinIncidentDurationSeconds, Math.Max(_options.MinIncidentDurationSeconds, _options.MaxIncidentDurationSeconds)));

    private TimeSpan RandomGap() => TimeSpan.FromSeconds(
        Rng.Between(_options.MinSecondsBetweenIncidents, Math.Max(_options.MinSecondsBetweenIncidents, _options.MaxSecondsBetweenIncidents)));

    private DateTime AlignUp(DateTime at)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.MetricsIntervalSeconds)).Ticks;
        return new DateTime((at.Ticks + interval - 1) / interval * interval, DateTimeKind.Utc);
    }
}
