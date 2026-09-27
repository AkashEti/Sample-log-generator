using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// Defines how one incident type shows up in logs and metrics, plus its ground truth.
/// Each scenario has <see cref="Variants"/>: different causal structures or blast radii of the same failure, each of
/// which may override the ground truth below. Hooks read the running variant from <see cref="ActiveIncident.Variant"/>.
/// Hooks: OnStart (onset), OnBackground (periodic symptoms), RunOrder (per-request behaviour),
/// OnEnd (recovery) and Gauge (metric overrides).
///
/// The ground-truth properties (RootCause, ExpectedEvidence, Remediation...) are for evaluation only.
/// Emitted log lines must show evidence and symptoms, never the conclusion: an investigator has to infer
/// the root cause, so no message may state it (e.g. "rolled back because of a leak").
/// </summary>
internal abstract class IncidentScenarioBase
{
    public abstract IncidentScenario Scenario { get; }
    public abstract string Title { get; }

    /// <summary>The ways this incident can play out. The first one is the classic form.</summary>
    public abstract IReadOnlyList<ScenarioVariant> Variants { get; }
    public abstract string RootCauseService { get; }
    public abstract string RootCause { get; }
    public abstract IReadOnlyList<string> AffectedServices { get; }
    public abstract IReadOnlyList<EvidenceHint> ExpectedEvidence { get; }

    /// <summary>Propagation path, root cause first (see <see cref="IncidentRecord.CausalChain"/>).</summary>
    public virtual IReadOnlyList<CausalLink> CausalChain => [];

    /// <summary>What must not be in the logs (see <see cref="IncidentRecord.ExpectedAbsences"/>).</summary>
    public virtual IReadOnlyList<AbsenceCondition> ExpectedAbsences => [];

    /// <summary>Events logged by OnEnd; the validator checks none of them appear before the incident ends.</summary>
    public virtual IReadOnlyList<EvidenceHint> RecoveryEvidence => [];
    public abstract IReadOnlyList<string> Remediation { get; }
    public virtual bool IsSecurityTest => false;
    public virtual TimeSpan BackgroundInterval => TimeSpan.FromSeconds(15);

    /// <summary>Suppress a background noise event that would contradict this incident's evidence.</summary>
    public virtual bool SuppressesNoise(int eventId) => false;

    public virtual void OnStart(LogGenerator g, ActiveIncident incident, DateTime at) { }
    public virtual void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at) { }
    public virtual void RunOrder(Flow f, ActiveIncident incident) => CommonFlows.Normal(f);
    public virtual void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at) { }
    public virtual double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng) => null;
}

/// <summary>
/// One way a scenario can play out. Null ground-truth fields fall back to the scenario's defaults.
/// </summary>
internal sealed record ScenarioVariant(string Name, IncidentShape Shape)
{
    public string? RootCause { get; init; }
    public IReadOnlyList<string>? AffectedServices { get; init; }
    public IReadOnlyList<EvidenceHint>? ExpectedEvidence { get; init; }
    public IReadOnlyList<CausalLink>? CausalChain { get; init; }
    public IReadOnlyList<AbsenceCondition>? ExpectedAbsences { get; init; }
    public IReadOnlyList<EvidenceHint>? RecoveryEvidence { get; init; }
    public IReadOnlyList<string>? Remediation { get; init; }

    /// <summary>Hosts this variant singles out (the stale, impaired, crashing or canary instances).</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary>Cascades only: the last stage reached; the failure is contained before it spreads further.</summary>
    public int? MaxStage { get; init; }
}

internal sealed class ActiveIncident
{
    private static readonly long ImpactBucketTicks = TimeSpan.FromSeconds(10).Ticks;

    private readonly HashSet<string> _orderIds = [];
    private readonly HashSet<string> _correlationIds = [];
    private readonly Dictionary<(string Service, int EventId), DateTime> _firstSeen = [];
    private readonly HashSet<string> _touchedHosts = [];
    private readonly Dictionary<long, (int Count, DateTime First)> _impactBuckets = [];
    private DateTime? _firstImpact;
    private DateTime? _lastImpact;
    private HashSet<(string Service, int EventId)>? _evidenceKeys;

    public required IncidentRecord Record { get; init; }
    public required IncidentScenarioBase Scenario { get; init; }
    public required ScenarioVariant Variant { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime EndsAt { get; set; }
    public required DateTime NextBackgroundAt { get; set; }
    public int BackgroundTicks { get; set; }
    public Dictionary<string, string> State { get; } = [];

    private HashSet<(string Service, int EventId)> EvidenceKeys => _evidenceKeys ??=
        [.. Record.ExpectedEvidence.Select(e => (e.Service, e.EventId)), .. Record.CausalChain.Select(l => (l.Service, l.EventId))];

    public bool IsActiveAt(DateTime at) => at >= StartedAt && at < EndsAt;

    /// <summary>0 at the start of the incident, 1 at its planned end.</summary>
    public double Progress(DateTime at)
    {
        var total = (EndsAt - StartedAt).TotalSeconds;
        return total <= 0 ? 1 : Math.Clamp((at - StartedAt).TotalSeconds / total, 0, 1);
    }

    public void MarkAffected(OrderContext order)
    {
        if (_correlationIds.Add(order.CorrelationId))
            Record.AffectedRequestCount++;
        if (order.IsCreated)
            _orderIds.Add(order.OrderId);
    }

    /// <summary>Called for every log line emitted while the incident is active, to measure what really happened.</summary>
    public void Observe(LogEntry entry, RequestContext? request)
    {
        var key = (entry.Service, entry.EventId);
        if (!_firstSeen.TryGetValue(key, out var seen) || entry.Timestamp < seen)
            _firstSeen[key] = entry.Timestamp;

        var warningOrWorse = Levels.Rank(entry.Level) >= Levels.Rank(Levels.Warning);
        if (request is not null && _correlationIds.Contains(request.CorrelationId))
        {
            _touchedHosts.Add(entry.Host);
            if (!warningOrWorse) return;
            if (_firstImpact is null || entry.Timestamp < _firstImpact) _firstImpact = entry.Timestamp;
            if (_lastImpact is null || entry.Timestamp > _lastImpact) _lastImpact = entry.Timestamp;
            var bucket = entry.Timestamp.Ticks / ImpactBucketTicks;
            _impactBuckets[bucket] = _impactBuckets.TryGetValue(bucket, out var b)
                ? (b.Count + 1, entry.Timestamp < b.First ? entry.Timestamp : b.First)
                : (1, entry.Timestamp);
        }
        else if (warningOrWorse && EvidenceKeys.Contains(key))
        {
            _touchedHosts.Add(entry.Host);
        }
    }

    public void Complete(DateTime endedAt, IncidentStatus status)
    {
        // Ground truth only lists evidence that exists in the logs.
        bool Occurred(string service, int eventId) => _firstSeen.ContainsKey((service, eventId));
        Record.ExpectedEvidence = [.. Record.ExpectedEvidence.Where(e => Occurred(e.Service, e.EventId))];
        Record.CausalChain = [.. Record.CausalChain
            .Where(l => Occurred(l.Service, l.EventId))
            .Select(l => l with { FirstSeenAt = _firstSeen[(l.Service, l.EventId)] })];
        Record.RecoveryEvidence = status == IncidentStatus.Resolved
            ? [.. Record.RecoveryEvidence.Where(e => Occurred(e.Service, e.EventId))]
            : [];

        var affectedServiceHosts = Record.AffectedServices.SelectMany(Services.HostsFor).ToList();
        Record.AffectedHosts = [.. affectedServiceHosts.Where(_touchedHosts.Contains)];
        Record.UnaffectedHosts = [.. affectedServiceHosts.Where(h => !_touchedHosts.Contains(h))];

        // The fix lands at endedAt; requests already in flight can still fail after it, and the incident is over only then.
        var recoveryAt = LogGenerator.TruncateToMilliseconds(endedAt);
        Record.EndedAt = _lastImpact > recoveryAt ? _lastImpact : recoveryAt;
        Record.Status = status;
        Record.Timeline = new IncidentTimeline
        {
            RootCauseAt = Record.CausalChain.FirstOrDefault()?.FirstSeenAt,
            FirstImpactAt = _firstImpact,
            // The earliest failure inside the busiest window, so it can never precede the first impact.
            PeakImpactAt = _impactBuckets.Count == 0 ? null : _impactBuckets.MaxBy(b => (b.Value.Count, -b.Key)).Value.First,
            LastImpactAt = _lastImpact,
            RecoveryStartedAt = status == IncidentStatus.Resolved ? recoveryAt : null,
        };
        Record.Difficulty = DifficultyOf(Record);
        Record.AffectedOrderIds = [.. _orderIds];
        Record.AffectedCorrelationIds = [.. _correlationIds];
    }

    /// <summary>
    /// The hardest feature present wins: hostile content (5), a plausible non-causal explanation (4), a cascade of three
    /// or more observed hops (3), other decoys (2), otherwise clean (1).
    /// </summary>
    internal static IncidentDifficulty DifficultyOf(IncidentRecord record)
    {
        var kinds = record.Distractors.Select(d => d.Kind).ToHashSet();
        if (record.IsSecurityTest || kinds.Contains(DistractorKind.SecurityNoise)) return IncidentDifficulty.Adversarial;
        if (kinds.Contains(DistractorKind.CompetingHypothesis) || kinds.Contains(DistractorKind.CorrelatedNotCausal))
            return IncidentDifficulty.CompetingHypotheses;
        if (record.Shape == IncidentShape.Cascade && record.CausalChain.Count >= 4) return IncidentDifficulty.Cascade;
        return kinds.Count > 0 ? IncidentDifficulty.Distractors : IncidentDifficulty.Clean;
    }
}
