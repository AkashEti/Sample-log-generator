using SampleLogGenerator.Configuration;
using SampleLogGenerator.Models;
using SampleLogGenerator.Simulation.Scenarios;

namespace SampleLogGenerator.Simulation;

/// <summary>How to run one incident. Unset fields are chosen at random or taken from the options.</summary>
public sealed record IncidentPlan
{
    public TimeSpan? Duration { get; init; }

    /// <summary>A variant name (see GET /scenarios); otherwise a random variant, of <see cref="Shape"/> when set.</summary>
    public string? Variant { get; init; }
    public IncidentShape? Shape { get; init; }

    /// <summary>Decoys to emit; defaults to <see cref="LogGeneratorOptions.DistractorsPerIncident"/>.</summary>
    public int? Distractors { get; init; }

    /// <summary>Decoy kinds to rotate through; defaults to <see cref="LogGeneratorOptions.DistractorKinds"/>.</summary>
    public IReadOnlyList<DistractorKind>? DistractorKinds { get; init; }

    /// <summary>Recorded in the ground truth when the incident was planned from an evaluation mix.</summary>
    public EvaluationProfile? Profile { get; init; }

    /// <summary>Hostile, instruction-like text in the logs on top of the outage; marks the incident as a security test.</summary>
    public bool Adversarial { get; init; }
}

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
    private readonly Dictionary<string, List<(DateTime From, string Version)>> _hostVersions = [];
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
            batch.Metrics.AddRange(_metrics.Snapshot(_nextMetricsAt, (int)interval.TotalSeconds, _active, Rng));
            batch.Metrics.Add(new MetricSample
            {
                Timestamp = _nextMetricsAt, Service = "Platform", Metric = "active_users", Value = _traffic.ActiveUsers, Unit = "count",
                Type = MetricTypes.Gauge, Environment = _options.Environment,
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
    public IncidentRecord StartIncident(IncidentScenario scenario, DateTime at, TimeSpan? duration = null, int? distractors = null) =>
        StartIncident(scenario, at, new IncidentPlan { Duration = duration, Distractors = distractors });

    public IncidentRecord StartIncident(IncidentScenario scenario, DateTime at, IncidentPlan plan)
    {
        if (_active is not null)
            throw new InvalidOperationException($"Incident {_active.Record.IncidentId} ({_active.Record.Scenario}) is still active.");
        if (!_started) Initialize(at);

        var definition = _scenarios[scenario];
        var variant = PickVariant(definition, plan);
        var incidentDuration = plan.Duration ?? RandomDuration();
        var record = new IncidentRecord
        {
            IncidentId = $"INC-{at:yyyyMMdd-HHmmss}-{Rng.Hex(4).ToUpperInvariant()}",
            Scenario = scenario,
            Title = definition.Title,
            Variant = variant.Name,
            Shape = variant.Shape,
            Profile = plan.Profile,
            StartedAt = TruncateToMilliseconds(at),
            RootCauseService = definition.RootCauseService,
            RootCause = variant.RootCause ?? definition.RootCause,
            AffectedServices = variant.AffectedServices ?? definition.AffectedServices,
            ExpectedEvidence = variant.ExpectedEvidence ?? definition.ExpectedEvidence,
            CausalChain = variant.CausalChain ?? definition.CausalChain,
            ExpectedAbsences = variant.ExpectedAbsences ?? definition.ExpectedAbsences,
            RecoveryEvidence = variant.RecoveryEvidence ?? definition.RecoveryEvidence,
            Remediation = variant.Remediation ?? definition.Remediation,
            IsSecurityTest = definition.IsSecurityTest || plan.Adversarial,
        };

        var decoys = PickDistractors(record, plan.Distractors ?? _options.DistractorsPerIncident,
            plan.DistractorKinds is { Count: > 0 } kinds ? [.. kinds.Distinct()] : EnabledDistractorKinds());
        var decoyTimes = decoys.Select(d => at.AddSeconds(DecoyOffsetSeconds(d.Kind, incidentDuration))).ToList();
        record.Distractors.AddRange(decoys.Zip(decoyTimes, (d, t) => d.ToRecord(t)));
        record.Difficulty = Scenarios.ActiveIncident.DifficultyOf(record); // provisional; final once the observed chain is known

        _active = new ActiveIncident
        {
            Record = record,
            Scenario = definition,
            Variant = variant,
            StartedAt = at,
            EndsAt = at + incidentDuration,
            NextBackgroundAt = at + definition.BackgroundInterval,
        };
        definition.OnStart(this, _active, at);

        foreach (var (decoy, decoyAt) in decoys.Zip(decoyTimes))
            EmitDecoy(decoy, decoyAt);

        return record;
    }

    private ScenarioVariant PickVariant(IncidentScenarioBase definition, IncidentPlan plan)
    {
        if (plan.Variant is { } name)
            return definition.Variants.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"{definition.Scenario} has no variant '{name}'. Variants: {string.Join(", ", definition.Variants.Select(v => v.Name))}");
        var candidates = plan.Shape is { } shape ? definition.Variants.Where(v => v.Shape == shape).ToList() : [.. definition.Variants];
        if (candidates.Count == 0)
            throw new ArgumentException($"{definition.Scenario} has no {plan.Shape} variant.");
        return Rng.Pick(candidates);
    }

    /// <summary>
    /// Decoys drawn in rotation from the given kinds. A decoy never shares a (service, event id) pair with the
    /// incident's evidence, chain or recovery events, so it cannot turn into accidental causal evidence.
    /// </summary>
    private List<Distractor> PickDistractors(IncidentRecord record, int count, List<DistractorKind> kinds)
    {
        if (count <= 0 || kinds.Count == 0) return [];
        var reserved = record.ExpectedEvidence.Select(e => (e.Service, e.EventId))
            .Concat(record.CausalChain.Select(l => (l.Service, l.EventId)))
            .Concat(record.RecoveryEvidence.Select(e => (e.Service, e.EventId)))
            .ToHashSet();
        var offset = Rng.Next(kinds.Count); // rotate from a random kind, so every kind gets used
        var picked = new List<Distractor>();
        for (var i = 0; i < count; i++)
        {
            var kind = kinds[(offset + i) % kinds.Count];
            var candidates = Distractor.All
                .Where(d => d.Kind == kind && !reserved.Contains((d.Service, d.EventId)) && !picked.Contains(d))
                .ToList();
            if (candidates.Count > 0) picked.Add(Rng.Pick(candidates));
        }
        return picked;
    }

    private List<DistractorKind> EnabledDistractorKinds() =>
        _options.DistractorKinds.Count > 0 ? [.. _options.DistractorKinds.Distinct()] : [.. Enum.GetValues<DistractorKind>()];

    /// <summary>Correlated-not-causal decoys hug the onset; the rest follow the configured timing.</summary>
    private double DecoyOffsetSeconds(DistractorKind kind, TimeSpan incidentDuration)
    {
        var seconds = incidentDuration.TotalSeconds;
        var timing = kind == DistractorKind.CorrelatedNotCausal ? DistractorTiming.Onset : _options.DistractorTiming;
        var window = timing switch
        {
            DistractorTiming.Onset => Math.Min(15, seconds),
            DistractorTiming.Spread => seconds * 0.9,
            _ => Math.Min(90, seconds * 0.4),
        };
        return Rng.Between(0.0, Math.Max(1, window));
    }

    /// <summary>Emits a decoy, repeated <see cref="LogGeneratorOptions.DistractorIntensity"/> times.</summary>
    private void EmitDecoy(Distractor decoy, DateTime at)
    {
        var repeats = Math.Clamp(_options.DistractorIntensity, 1, 3);
        for (var i = 0; i < repeats; i++)
        {
            decoy.Emit(this, at);
            at = at.AddSeconds(Rng.Between(15, 45));
        }
    }

    /// <summary>
    /// Negative controls: decoys with no incident at all. The investigator's right answer is "no incident".
    /// </summary>
    public DistractorRecord InjectDistractor(DistractorKind kind, DateTime at)
    {
        if (!_started) Initialize(at);
        var decoy = Rng.Pick(Distractor.All.Where(d => d.Kind == kind).ToList());
        EmitDecoy(decoy, at);
        return decoy.ToRecord(at);
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
        RequestContext? request = null, string? exception = null, int? durationMs = null, string? host = null,
        OperationInfo? operation = null)
    {
        var resolvedHost = host ?? request?.HostFor(service, Rng) ?? Rng.Pick(Services.HostsFor(service));
        var truncated = TruncateToMilliseconds(timestamp);
        var trace = request?.SpanFor(service, Rng);
        var entry = new LogEntry
        {
            Timestamp = truncated,
            Level = level,
            Service = service,
            Host = resolvedHost,
            Region = Services.Region,
            AvailabilityZone = Services.ZoneFor(resolvedHost),
            Version = VersionAt(service, resolvedHost, truncated),
            Environment = _options.Environment,
            EventId = eventId,
            Message = message,
            TraceId = trace?.TraceId,
            SpanId = trace?.SpanId,
            ParentSpanId = trace?.ParentSpanId,
            UpstreamService = trace?.UpstreamService,
            CorrelationId = request?.CorrelationId,
            RequestId = request?.RequestIdFor(service, Rng),
            SessionId = request?.SessionId,
            UserId = request?.UserId,
            OrderId = request is OrderContext { IsCreated: true } order ? order.OrderId : null,
            DurationMs = durationMs,
            Operation = operation is null ? null : operation with
            {
                StartedAt = TruncateToMilliseconds(operation.StartedAt),
                TimeoutAt = operation.TimeoutAt is { } timeoutAt ? TruncateToMilliseconds(timeoutAt) : null,
            },
            Exception = exception,
        };
        _pending.Enqueue(entry, (entry.Timestamp, _sequence++));
        // Compare with the truncated start too: an event logged at the very start instant must count as observed.
        if (_active is not null && entry.Timestamp >= TruncateToMilliseconds(_active.StartedAt)) _active.Observe(entry, request);
    }

    /// <summary>A deploy: from <paramref name="from"/> on, <paramref name="host"/> runs <paramref name="version"/>.</summary>
    internal void SetVersion(string host, DateTime from, string version)
    {
        if (!_hostVersions.TryGetValue(host, out var history)) _hostVersions[host] = history = [];
        history.Add((TruncateToMilliseconds(from), version));
        history.Sort((a, b) => a.From.CompareTo(b.From));
    }

    private string VersionAt(string service, string host, DateTime at)
    {
        var version = Services.InitialVersions[service];
        if (_hostVersions.TryGetValue(host, out var history))
            foreach (var (from, v) in history)
                if (from <= at) version = v;
        return version;
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
