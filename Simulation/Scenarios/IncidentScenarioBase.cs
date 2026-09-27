using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// Defines how one incident type shows up in logs and metrics, plus its ground truth.
/// Hooks: OnStart (onset), OnBackground (periodic symptoms), RunOrder (per-request behaviour),
/// OnEnd (recovery) and Gauge (metric overrides).
/// </summary>
internal abstract class IncidentScenarioBase
{
    public abstract IncidentScenario Scenario { get; }
    public abstract string Title { get; }
    public abstract string RootCauseService { get; }
    public abstract string RootCause { get; }
    public abstract IReadOnlyList<string> AffectedServices { get; }
    public abstract IReadOnlyList<EvidenceHint> ExpectedEvidence { get; }
    public abstract IReadOnlyList<string> Remediation { get; }
    public virtual bool IsSecurityTest => false;
    public virtual TimeSpan BackgroundInterval => TimeSpan.FromSeconds(15);

    public virtual void OnStart(LogGenerator g, ActiveIncident incident, DateTime at) { }
    public virtual void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at) { }
    public virtual void RunOrder(Flow f, ActiveIncident incident) => CommonFlows.Normal(f);
    public virtual void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at) { }
    public virtual double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng) => null;
}

internal sealed class ActiveIncident
{
    private readonly HashSet<string> _orderIds = [];
    private readonly HashSet<string> _correlationIds = [];

    public required IncidentRecord Record { get; init; }
    public required IncidentScenarioBase Scenario { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime EndsAt { get; set; }
    public required DateTime NextBackgroundAt { get; set; }
    public int BackgroundTicks { get; set; }
    public Dictionary<string, string> State { get; } = [];

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

    public void Complete(DateTime endedAt, IncidentStatus status)
    {
        Record.EndedAt = LogGenerator.TruncateToMilliseconds(endedAt);
        Record.Status = status;
        Record.AffectedOrderIds = [.. _orderIds];
        Record.AffectedCorrelationIds = [.. _correlationIds];
    }
}
