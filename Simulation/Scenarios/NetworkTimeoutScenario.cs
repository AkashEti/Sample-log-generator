using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

internal sealed class NetworkTimeoutScenario : IncidentScenarioBase
{
    private const string Endpoint = "payment-service.internal:8443";
    private const string TimeoutException =
        "System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.\n" +
        " ---> System.Net.Sockets.SocketException (110): Connection timed out\n" +
        "   at OrderService.Clients.PaymentClient.ChargeAsync(ChargeRequest request, CancellationToken ct)";

    public override IncidentScenario Scenario => IncidentScenario.NetworkTimeout;
    public override string Title => "Network timeouts between OrderService and PaymentService";
    public override string RootCauseService => Services.Order;
    public override string RootCause =>
        $"Network connectivity problem (packet loss) between OrderService and PaymentService ({Endpoint}); calls time out before reaching PaymentService, which logs nothing for the failed orders.";
    public override IReadOnlyList<string> AffectedServices => [Services.Order, Services.Payment, Services.Inventory];
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        new(Services.Order, EventIds.PaymentUpstreamDegraded, "Elevated connect time / packet loss to PaymentService"),
        new(Services.Order, EventIds.PaymentCallTimeout, "SocketException: Connection timed out calling PaymentService"),
        new(Services.Payment, 0, "No PaymentService logs exist for the affected orders (the requests never arrived)"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Check network path, security groups and DNS between OrderService and PaymentService",
        "Inspect service mesh/load balancer health for payment-service.internal",
        "Add connect-timeout alerts per upstream dependency",
    ];

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at) => ReportDegraded(g, at);

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at) => ReportDegraded(g, at);

    private static void ReportDegraded(LogGenerator g, DateTime at) =>
        g.Emit(at, Levels.Warning, Services.Order, EventIds.PaymentUpstreamDegraded,
            $"Upstream {Endpoint} degraded: TCP connect time {g.Rng.Between(2500, 9000)} ms, packet loss {g.Rng.Between(35, 70)}%");

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        if (!f.Rng.Chance(0.7))
        {
            CommonFlows.Normal(f);
            return;
        }

        var o = f.Order;
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

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at) =>
        g.Emit(at, Levels.Information, Services.Order, EventIds.PaymentConnectivityRestored,
            $"Connectivity to {Endpoint} restored: TCP connect time {g.Rng.Between(1, 4)} ms");

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng) =>
        (service, metric) switch
        {
            (Services.Order, "upstream_payment_latency_p95_ms") => 10000,
            _ => null
        };
}
