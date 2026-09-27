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
    public IncidentStatus Status { get; set; } = IncidentStatus.Active;
    public required DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; set; }
    public required string RootCauseService { get; init; }
    public required string RootCause { get; init; }
    public required IReadOnlyList<string> AffectedServices { get; init; }
    public required IReadOnlyList<EvidenceHint> ExpectedEvidence { get; init; }
    public required IReadOnlyList<string> Remediation { get; init; }
    public bool IsSecurityTest { get; init; }
    public int AffectedRequestCount { get; set; }
    public List<string> AffectedOrderIds { get; set; } = [];
    public List<string> AffectedCorrelationIds { get; set; } = [];
}

/// <summary>A log event an investigator is expected to find and cite.</summary>
public sealed record EvidenceHint(string Service, int EventId, string Description);
