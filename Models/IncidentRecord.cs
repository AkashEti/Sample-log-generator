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
    public required IncidentDifficulty Difficulty { get; init; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Active;
    public required DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; set; }
    public required string RootCauseService { get; init; }
    public required string RootCause { get; init; }
    public required IReadOnlyList<string> AffectedServices { get; init; }
    /// <summary>Evidence the investigator should find. Only events that actually occurred during this incident are listed.</summary>
    public required IReadOnlyList<EvidenceHint> ExpectedEvidence { get; set; }

    /// <summary>
    /// How the failure propagated, in cause-to-effect order with the root cause first; the root cause is also the first
    /// link to appear in the logs. Later links can overlap in time (effects driven by user requests may show up after the
    /// next stage has begun), and links that never occurred (no user hit that path) are left out.
    /// Scores whether the investigator traced the chain back to its origin rather than stopping at the loudest symptom.
    /// </summary>
    public IReadOnlyList<EvidenceHint> CausalChain { get; set; } = [];

    /// <summary>Unrelated events injected to compete with the real cause. Blaming one of these is a wrong answer.</summary>
    public List<EvidenceHint> Distractors { get; init; } = [];
    public required IReadOnlyList<string> Remediation { get; init; }
    public bool IsSecurityTest { get; init; }
    public int AffectedRequestCount { get; set; }
    public List<string> AffectedOrderIds { get; set; } = [];
    public List<string> AffectedCorrelationIds { get; set; } = [];
}

/// <summary>A log event an investigator is expected to find and cite.</summary>
public sealed record EvidenceHint(string Service, int EventId, string Description);
