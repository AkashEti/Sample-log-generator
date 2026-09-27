using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// Partial network failure: only the OrderService instances in one zone lose connectivity to PaymentService.
/// <code>
/// order-1 (zone a) ──► PaymentService OK
/// order-2/3 (zone b) ──► connect timeouts ──► retries ──► orders fail; PaymentService never sees these requests
/// </code>
/// Key evidence includes an absence: no PaymentService log lines exist for the failed orders.
/// </summary>
internal sealed class NetworkTimeoutScenario : IncidentScenarioBase
{
    private const string Endpoint = "payment-service.internal:8443";
    private const string TimeoutException =
        "System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.\n" +
        " ---> System.Net.Sockets.SocketException (110): Connection timed out\n" +
        "   at OrderService.Clients.PaymentClient.ChargeAsync(ChargeRequest request, CancellationToken ct)";

    private static IReadOnlyList<string> OrderHosts => Services.HostsFor(Services.Order);
    private static string HealthyHost => OrderHosts[0];
    private static IReadOnlyList<string> IsolatedHosts => [.. OrderHosts.Skip(1)];

    public override IncidentScenario Scenario => IncidentScenario.NetworkTimeout;
    public override string Title => "Network timeouts between OrderService and PaymentService";
    public override IncidentDifficulty Difficulty => IncidentDifficulty.Distributed;
    public override string RootCauseService => Services.Order;
    public override string RootCause =>
        $"Network connectivity problem (packet loss) between the OrderService instances {IsolatedHosts[0]} and {IsolatedHosts[1]} and PaymentService ({Endpoint}); " +
        $"their calls time out before reaching PaymentService, which logs nothing for the failed orders. {HealthyHost} is unaffected.";
    public override IReadOnlyList<string> AffectedServices => [Services.Order, Services.Payment, Services.Inventory];
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        new(Services.Order, EventIds.PaymentUpstreamDegraded, "Elevated connect time / packet loss to PaymentService, reported only by some OrderService hosts"),
        new(Services.Order, EventIds.PaymentCallTimeout, "SocketException: Connection timed out calling PaymentService, only from the same hosts"),
        new(Services.Payment, 0, "No PaymentService logs exist for the affected orders (the requests never arrived)"),
    ];
    public override IReadOnlyList<EvidenceHint> CausalChain =>
    [
        new(Services.Order, EventIds.PaymentUpstreamDegraded, "Packet loss from two OrderService hosts to PaymentService"),
        new(Services.Order, EventIds.PaymentCallTimeout, "Connect timeouts calling PaymentService"),
        new(Services.Order, EventIds.OrderFailedPaymentUnreachable, "Orders fail: PaymentService unreachable"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Check network path, security groups and routing from the affected OrderService hosts to PaymentService",
        "Drain or reschedule the affected OrderService instances",
        "Add connect-timeout alerts per upstream dependency and per instance",
    ];

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at) => ReportDegraded(g, at);

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at) => ReportDegraded(g, at);

    private static void ReportDegraded(LogGenerator g, DateTime at)
    {
        foreach (var host in IsolatedHosts)
        {
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 2000)), Levels.Warning, Services.Order, EventIds.PaymentUpstreamDegraded,
                $"Upstream {Endpoint} degraded: TCP connect time {g.Rng.Between(2500, 9000)} ms, packet loss {g.Rng.Between(35, 70)}%", host: host);
        }
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        var host = f.Rng.Pick(OrderHosts);
        o.PinHost(Services.Order, host);
        if (host == HealthyHost || !f.Rng.Chance(0.9))
        {
            CommonFlows.Normal(f);
            return;
        }

        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);
        if (!CommonFlows.ReserveInventory(f)) return;
        incident.MarkAffected(o);

        f.Wait(10000).Error(Services.Order, EventIds.PaymentCallTimeout, $"HTTP request to PaymentService timed out for order {o.OrderId}", TimeoutException, 10000)
         .Wait(500, 1500).Warning(Services.Order, EventIds.PaymentCallRetry, $"Retrying PaymentService call for order {o.OrderId} (attempt 2/2)")
         .Wait(10000).Error(Services.Order, EventIds.PaymentCallTimeout, $"HTTP request to PaymentService timed out for order {o.OrderId}", TimeoutException, 10000)
         .Wait(5, 20).Error(Services.Order, EventIds.OrderFailedPaymentUnreachable, $"Order {o.OrderId} failed: PaymentService unreachable");
        CommonFlows.ReleaseInventory(f);
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in IsolatedHosts)
        {
            g.Emit(at, Levels.Information, Services.Order, EventIds.PaymentConnectivityRestored,
                $"Connectivity to {Endpoint} restored: TCP connect time {g.Rng.Between(1, 4)} ms", host: host);
            at = at.AddMilliseconds(g.Rng.Between(200, 1500));
        }
    }

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng) =>
        (service, metric) switch
        {
            (Services.Order, "upstream_payment_latency_p95_ms") => 10000,
            _ => null
        };
}
