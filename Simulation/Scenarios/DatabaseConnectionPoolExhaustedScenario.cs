using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

internal sealed class DatabaseConnectionPoolExhaustedScenario : IncidentScenarioBase
{
    private const int MaxPoolSize = 100;
    private const string BadVersion = "2.14.0";
    private const string PreviousVersion = "2.13.2";
    private const string PoolException =
        "System.InvalidOperationException: Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool. " +
        "This may have occurred because all pooled connections were in use and max pool size was reached.\n" +
        "   at Npgsql.ConnectorPool.Get(NpgsqlConnection conn, NpgsqlTimeout timeout, Boolean async, CancellationToken ct)\n" +
        "   at PaymentService.Data.PaymentRepository.SaveTransactionAsync(PaymentTransaction tx, CancellationToken ct)";

    public override IncidentScenario Scenario => IncidentScenario.DatabaseConnectionPoolExhausted;
    public override string Title => "Payments database connection pool exhausted";
    public override string RootCauseService => Services.Payment;
    public override string RootCause =>
        $"A connection leak introduced in the PaymentService v{BadVersion} deployment exhausted the payments-db connection pool (max {MaxPoolSize}); payments cannot be persisted.";
    public override IReadOnlyList<string> AffectedServices => [Services.Payment, Services.Order, Services.Inventory];
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        new(Services.Payment, EventIds.ServiceDeployed, $"PaymentService v{BadVersion} deployed just before the incident"),
        new(Services.Payment, EventIds.ConnectionPoolUsageHigh, "Connection pool usage climbing"),
        new(Services.Payment, EventIds.ConnectionPoolExhausted, "Connection pool exhausted"),
        new(Services.Payment, EventIds.PaymentPersistFailed, "Timeout obtaining a connection from the pool"),
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
                $"PaymentService v{BadVersion} started (build 8841, previous v{PreviousVersion})", host: host);
            at = at.AddMilliseconds(g.Rng.Between(800, 2500));
        }
    }

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        var usage = Usage(incident, at);
        if (usage >= MaxPoolSize)
            g.Emit(at, Levels.Error, Services.Payment, EventIds.ConnectionPoolExhausted,
                $"Connection pool exhausted on payments-db: {MaxPoolSize}/{MaxPoolSize} connections in use, {g.Rng.Between(20, 60)} requests waiting");
        else if (usage >= 60)
            g.Emit(at, Levels.Warning, Services.Payment, EventIds.ConnectionPoolUsageHigh,
                $"Database connection pool usage high: {usage}/{MaxPoolSize} active connections (payments-db)");
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        var usage = Usage(incident, f.Cursor);
        var failureChance = usage >= MaxPoolSize ? 0.9 : usage >= 85 ? 0.3 : 0;
        if (!f.Rng.Chance(failureChance))
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

        f.Wait(15000).Error(Services.Payment, EventIds.PaymentPersistFailed, $"Failed to persist payment transaction for order {o.OrderId}", PoolException, 15000)
         .Wait(5, 20).Error(Services.Payment, EventIds.PaymentFailed, $"Payment failed for order {o.OrderId}: database unavailable")
         .Wait(5, 30).Warning(Services.Order, EventIds.PaymentPending, $"Payment for order {o.OrderId} still pending after 15 s")
         .Wait(2000, 5000).Error(Services.Order, EventIds.OrderPaymentFailed, $"Order {o.OrderId} marked as PaymentFailed");
        CommonFlows.ReleaseInventory(f);
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at) =>
        g.Emit(at, Levels.Information, Services.Payment, EventIds.ConnectionPoolRecovered,
            $"PaymentService rolled back to v{PreviousVersion}; connection pool usage {g.Rng.Between(8, 18)}/{MaxPoolSize}");

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
