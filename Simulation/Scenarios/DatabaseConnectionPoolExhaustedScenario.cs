using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

internal sealed class DatabaseConnectionPoolExhaustedScenario : IncidentScenarioBase
{
    private const int MaxPoolSize = 100;
    private const string BadVersion = "2.14.0";
    private const string PreviousVersion = "2.13.2";

    public override IncidentScenario Scenario => IncidentScenario.DatabaseConnectionPoolExhausted;
    private const string LeakingCaller = "RefundReconciliationJob.RunAsync";
    private const string ExhaustedAtKey = "exhaustedAtTicks";

    public override string Title => "Payments database connection pool exhausted";
    public override IncidentDifficulty Difficulty => IncidentDifficulty.Correlated;
    public override string RootCauseService => Services.Payment;
    public override string RootCause =>
        $"A connection leak introduced in the PaymentService v{BadVersion} deployment ({LeakingCaller} never returns its connections) " +
        $"exhausted the payments-db connection pool (max {MaxPoolSize}) on every instance; payments cannot be persisted.";
    public override IReadOnlyList<string> AffectedServices => [Services.Payment, Services.Order, Services.Inventory];
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        new(Services.Payment, EventIds.ServiceDeployed, $"PaymentService v{BadVersion} deployed just before pool usage starts climbing"),
        new(Services.Payment, EventIds.ConnectionPoolUsageHigh, "Connection pool usage climbing on every instance"),
        new(Services.Payment, EventIds.ConnectionHeldTooLong, $"Connections opened by {LeakingCaller} are never returned to the pool"),
        new(Services.Payment, EventIds.ConnectionPoolExhausted, "Connection pool exhausted"),
        new(Services.Payment, EventIds.PaymentPersistFailed, "Timeout obtaining a connection from the pool"),
    ];
    public override IReadOnlyList<EvidenceHint> CausalChain =>
    [
        new(Services.Payment, EventIds.ServiceDeployed, $"PaymentService v{BadVersion} deployed"),
        new(Services.Payment, EventIds.ConnectionHeldTooLong, "Connections never returned to the pool"),
        new(Services.Payment, EventIds.ConnectionPoolExhausted, "Pool exhausted"),
        new(Services.Payment, EventIds.PaymentPersistFailed, "Payments cannot be persisted"),
        new(Services.Order, EventIds.OrderPaymentFailed, "Orders marked PaymentFailed"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        $"Roll back PaymentService to v{PreviousVersion}",
        "Find the undisposed DbConnection/DbContext introduced in the release",
        "Alert on connection pool utilisation before it reaches 100%",
    ];
    public override TimeSpan BackgroundInterval => TimeSpan.FromSeconds(10);

    /// <summary>Pool usage ramps from ~20 to 100 over the first 40% of the incident.</summary>
    private static int Usage(ActiveIncident incident, DateTime at) =>
        (int)Math.Min(MaxPoolSize, 20 + 80 * Math.Min(1, incident.Progress(at) / 0.4));

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in Services.HostsFor(Services.Payment))
        {
            g.Emit(at, Levels.Information, Services.Payment, EventIds.ServiceDeployed,
                $"PaymentService v{BadVersion} started (build 8841, previous v{PreviousVersion}; release notes: nightly refund reconciliation)", host: host);
            at = at.AddMilliseconds(g.Rng.Between(800, 2500));
        }
    }

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        // Every instance runs the leaking build, so each one reports its own pool.
        foreach (var host in Services.HostsFor(Services.Payment))
        {
            var hostAt = at.AddMilliseconds(g.Rng.Between(0, 2000));
            var usage = Math.Min(MaxPoolSize, Usage(incident, hostAt) + g.Rng.Between(-4, 4));
            if (usage >= MaxPoolSize)
            {
                // From the first reported exhaustion on, requests start failing (see RunOrder).
                if (!incident.State.ContainsKey(ExhaustedAtKey)) incident.State[ExhaustedAtKey] = hostAt.Ticks.ToString();
                g.Emit(hostAt, Levels.Error, Services.Payment, EventIds.ConnectionPoolExhausted,
                    $"Connection pool exhausted on payments-db: {MaxPoolSize}/{MaxPoolSize} connections in use, {g.Rng.Between(20, 60)} requests waiting", host: host);
            }
            else if (usage >= 60)
                g.Emit(hostAt, Levels.Warning, Services.Payment, EventIds.ConnectionPoolUsageHigh,
                    $"Database connection pool usage high: {usage}/{MaxPoolSize} active connections (payments-db)", host: host);
        }

        // The pool's leak detector names the caller holding connections: evidence, not a verdict.
        if (Usage(incident, at) >= 40)
        {
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 3000)), Levels.Warning, Services.Payment, EventIds.ConnectionHeldTooLong,
                $"Connection to payments-db held for {g.Rng.Between(120, 900)} s without being returned to the pool (opened by {LeakingCaller})");
        }
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        // Requests only fail once the pool is exhausted; before that they merely wait longer for a connection.
        var exhausted = incident.State.TryGetValue(ExhaustedAtKey, out var ticks) && f.Cursor >= new DateTime(long.Parse(ticks), DateTimeKind.Utc);
        if (!exhausted || !f.Rng.Chance(0.9))
        {
            CommonFlows.Normal(f);
            return;
        }

        var o = f.Order;
        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);
        if (!CommonFlows.ReserveInventory(f)) return;
        CommonFlows.StartPayment(f);
        incident.MarkAffected(o);

        f.Wait(15000).Error(Services.Payment, EventIds.PaymentPersistFailed, $"Failed to persist payment transaction for order {o.OrderId}", Exceptions.PoolTimeout, 15000)
         .Wait(5, 20).Error(Services.Payment, EventIds.PaymentFailed, $"Payment failed for order {o.OrderId}: database unavailable")
         .Wait(5, 30).Warning(Services.Order, EventIds.PaymentPending, $"Payment for order {o.OrderId} still pending after 15 s")
         .Wait(2000, 5000).Error(Services.Order, EventIds.OrderPaymentFailed, $"Order {o.OrderId} marked as PaymentFailed");
        CommonFlows.ReleaseInventory(f);
    }

    // Recovery is visible only as a redeploy of the previous build and a healthy pool; the logs never say why.
    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in Services.HostsFor(Services.Payment))
        {
            g.Emit(at, Levels.Information, Services.Payment, EventIds.ServiceDeployed,
                $"PaymentService v{PreviousVersion} started (build 8790, previous v{BadVersion})", host: host);
            g.Emit(at.AddSeconds(g.Rng.Between(5, 15)), Levels.Information, Services.Payment, EventIds.ConnectionPoolRecovered,
                $"Database connection pool usage normal: {g.Rng.Between(8, 18)}/{MaxPoolSize} active connections (payments-db)", host: host);
            at = at.AddMilliseconds(g.Rng.Between(800, 2500));
        }
    }

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng)
    {
        if (service != Services.Payment) return null;
        var usage = Usage(incident, at);
        return metric switch
        {
            "db_connections_active" => usage,
            "db_connection_wait_ms" => usage >= MaxPoolSize ? rng.Between(12000, 15000) : usage >= 80 ? rng.Between(400, 1500) : null,
            _ => null
        };
    }
}
