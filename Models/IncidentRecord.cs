namespace SampleLogGenerator.Models;

/// <summary>
/// Ground truth for an injected incident. Compare the investigator's diagnosis against this during evaluation.
/// Never expose it to the investigator itself.
/// </summary>
public sealed class IncidentRecord
{
    public required string IncidentId { get; init; }
    public required IncidentScenario Scenario { get; init; }
    public required string Title { get; init; }

    /// <summary>Which variant of the scenario ran; variants of one scenario have different causal structures.</summary>
    public required string Variant { get; init; }
    public required IncidentShape Shape { get; init; }

    /// <summary>The evaluation bucket this incident was planned for, when the dataset was generated with a mix.</summary>
    public EvaluationProfile? Profile { get; init; }

    /// <summary>Set when the incident completes, from the shape, the observed chain and the decoys.</summary>
    public IncidentDifficulty Difficulty { get; set; } = IncidentDifficulty.Clean;
    public IncidentStatus Status { get; set; } = IncidentStatus.Active;
    public required DateTime StartedAt { get; init; }

    /// <summary>
    /// When the incident was over: the later of the fix taking effect (<see cref="IncidentTimeline.RecoveryStartedAt"/>)
    /// and the last failure of a request that was already in flight.
    /// </summary>
    public DateTime? EndedAt { get; set; }
    public IncidentTimeline Timeline { get; set; } = new();
    public required string RootCauseService { get; init; }
    public required string RootCause { get; init; }
    public required IReadOnlyList<string> AffectedServices { get; init; }

    /// <summary>Instances of the affected services that handled failing requests or logged warning-level evidence.</summary>
    public List<string> AffectedHosts { get; set; } = [];

    /// <summary>Instances of the affected services that stayed healthy (the other half of a partial failure).</summary>
    public List<string> UnaffectedHosts { get; set; } = [];

    /// <summary>Evidence the investigator should find. Only events that actually occurred during this incident are listed.</summary>
    public required IReadOnlyList<EvidenceHint> ExpectedEvidence { get; set; }

    /// <summary>
    /// How the failure propagated, in cause-to-effect order with the root cause first; see <see cref="CausalLink"/> for the
    /// ordering rule. Links that never occurred (no user hit that path) are left out.
    /// </summary>
    public IReadOnlyList<CausalLink> CausalChain { get; set; } = [];

    /// <summary>Things that must NOT be in the logs; evidence by absence (e.g. PaymentService never saw the failed requests).</summary>
    public IReadOnlyList<AbsenceCondition> ExpectedAbsences { get; set; } = [];

    /// <summary>Events that mark recovery. They must not appear before the incident ends.</summary>
    public IReadOnlyList<EvidenceHint> RecoveryEvidence { get; set; } = [];

    /// <summary>Unrelated events injected around the incident. Blaming one of these is a wrong answer.</summary>
    public List<DistractorRecord> Distractors { get; init; } = [];
    public required IReadOnlyList<string> Remediation { get; init; }
    /// <summary>True for prompt-injection incidents and for outages with an adversarial overlay (hostile text in the logs).</summary>
    public bool IsSecurityTest { get; init; }
    public int AffectedRequestCount { get; set; }
    public List<string> AffectedOrderIds { get; set; } = [];
    public List<string> AffectedCorrelationIds { get; set; } = [];
}

/// <summary>A log event an investigator is expected to find and cite, optionally only on specific hosts.</summary>
public sealed record EvidenceHint(string Service, int EventId, string Description, IReadOnlyList<string>? Hosts = null);

/// <summary>
/// One hop of the causal chain. Ordering rule, checked by the dataset validator: a link first appears no earlier than
/// the most recent preceding link that is not <see cref="Concurrent"/>. Concurrent links are effects of a stage that
/// user requests produce; with light traffic they can show up after the next stage has begun, so later links are not
/// ordered against them. The root cause (first link) is never concurrent.
/// </summary>
public sealed record CausalLink(string Service, int EventId, string Description, bool Concurrent = false)
{
    /// <summary>When this link first appeared in the logs (filled in when the incident completes).</summary>
    public DateTime? FirstSeenAt { get; init; }

    public EvidenceHint ToHint() => new(Service, EventId, Description);
}

public enum AbsenceKind
{
    /// <summary><see cref="AbsenceCondition.Service"/> logged nothing for any affected request (correlation id).</summary>
    NoLogsForAffectedRequests,

    /// <summary>The event never appears on <see cref="AbsenceCondition.Hosts"/> during the incident.</summary>
    NoEventOnHosts,
}

public sealed record AbsenceCondition(AbsenceKind Kind, string Service, string Description, int? EventId = null, IReadOnlyList<string>? Hosts = null);

/// <summary>
/// Key moments of the incident, measured from the logs that were actually generated. Invariants (checked by the
/// validator): StartedAt &lt;= RootCauseAt &lt;= FirstImpactAt &lt;= PeakImpactAt &lt;= LastImpactAt &lt;= EndedAt, and
/// RecoveryStartedAt &lt;= EndedAt.
/// </summary>
public sealed record IncidentTimeline
{
    /// <summary>First appearance of the root-cause link.</summary>
    public DateTime? RootCauseAt { get; init; }

    /// <summary>First warning-or-worse log of a request that failed because of the incident.</summary>
    public DateTime? FirstImpactAt { get; init; }

    /// <summary>First failing-request log inside the 10-second window with the most failing-request warnings/errors.</summary>
    public DateTime? PeakImpactAt { get; init; }

    /// <summary>
    /// Last such log. It can come after <see cref="RecoveryStartedAt"/>: requests already in flight when the fix lands
    /// still fail (e.g. waiting out a 30 s timeout). <see cref="IncidentRecord.EndedAt"/> covers them.
    /// </summary>
    public DateTime? LastImpactAt { get; init; }

    /// <summary>When the fix took effect and recovery logs start. Null if the incident was interrupted.</summary>
    public DateTime? RecoveryStartedAt { get; init; }
}

/// <summary>The five kinds of decoy event, from harmless to adversarial.</summary>
public enum DistractorKind
{
    /// <summary>Routine, low-salience events (GC, config reloads, brief benign slowdowns). Should simply be ignored.</summary>
    BenignNoise,

    /// <summary>Events that line up in time with the incident (autoscaling, log backpressure, crawler spikes) but did not cause it.</summary>
    CorrelatedNotCausal,

    /// <summary>Plausible alternative root causes (an unrelated deploy, flag rollout, cert warning) that must be ruled out.</summary>
    CompetingHypothesis,

    /// <summary>Alarming errors in unrelated places with no customer impact (disk warnings, a red archive index).</summary>
    MisleadingEvidence,

    /// <summary>Hostile or suspicious input in the logs (prompt-injection text, credential stuffing). Data, never instructions.</summary>
    SecurityNoise,
}

/// <summary>A decoy that was emitted. <see cref="At"/> is when it first appeared.</summary>
public sealed record DistractorRecord(DistractorKind Kind, string Service, int EventId, string Description, DateTime At);
