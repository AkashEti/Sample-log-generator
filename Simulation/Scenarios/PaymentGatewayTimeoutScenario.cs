using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

internal sealed class PaymentGatewayTimeoutScenario : IncidentScenarioBase
{
    private const string Gateway = "api.paygate-sim.example";
    private const string TimeoutException =
        "System.TimeoutException: The operation has timed out after 30000 ms.\n" +
        "   at PaymentService.Gateway.PayGateClient.AuthorizeAsync(PaymentRequest request, CancellationToken ct)\n" +
        "   at PaymentService.Payments.PaymentProcessor.ProcessAsync(Order order, CancellationToken ct)";

    public override IncidentScenario Scenario => IncidentScenario.PaymentGatewayTimeout;
    public override string Title => "Payment gateway timeouts";
    public override string RootCauseService => Services.Payment;
    public override string RootCause =>
        $"The external payment gateway ({Gateway}) is responding slowly; PaymentService calls hit the 30 s timeout, retries are exhausted and orders fail with PaymentFailed.";
    public override IReadOnlyList<string> AffectedServices => [Services.Payment, Services.Order, Services.Inventory];
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        new(Services.Payment, EventIds.GatewayLatencyHigh, "Gateway p95 latency above threshold"),
        new(Services.Payment, EventIds.GatewayTimeout, "TimeoutException calling the payment gateway"),
        new(Services.Order, EventIds.OrderPaymentFailed, "Orders marked as PaymentFailed"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Check the payment gateway status and latency",
        "Fail over to a secondary payment provider",
        "Add a circuit breaker and review timeout/retry policy for gateway calls",
    ];

    private static int Latency(ActiveIncident incident, DateTime at, Random rng) =>
        (int)(4000 + 26000 * Math.Min(1, incident.Progress(at) * 2)) + rng.Between(-500, 500);

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at) => ReportLatency(g, incident, at);

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at) => ReportLatency(g, incident, at);

    private static void ReportLatency(LogGenerator g, ActiveIncident incident, DateTime at) =>
        g.Emit(at, Levels.Warning, Services.Payment, EventIds.GatewayLatencyHigh,
            $"Payment gateway {Gateway} latency above threshold: p95 {Latency(incident, at, g.Rng)} ms (threshold 2000 ms)");

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);
        if (!CommonFlows.ReserveInventory(f)) return;
        CommonFlows.StartPayment(f);

        if (!f.Rng.Chance(0.8))
        {
            // Some requests still get through, just slowly.
            var slow = f.Rng.Between(6000, 14000);
            f.Wait(5000).Warning(Services.Payment, EventIds.GatewayResponseDelayed, $"Payment gateway response delayed for order {o.OrderId} (5000 ms elapsed)")
             .Wait(slow - 5000).Info(Services.Payment, EventIds.PaymentAuthorized, $"Payment authorized for order {o.OrderId} (transaction txn_{f.Rng.Hex(10)})", slow);
            CommonFlows.ConfirmAndNotify(f);
            return;
        }

        incident.MarkAffected(o);
        f.Wait(5000).Warning(Services.Payment, EventIds.GatewayResponseDelayed, $"Payment gateway response delayed for order {o.OrderId} (5000 ms elapsed)")
         .Wait(25000).Error(Services.Payment, EventIds.GatewayTimeout, $"Payment gateway request timed out for order {o.OrderId}", TimeoutException, 30000)
         .Wait(5, 30).Warning(Services.Order, EventIds.PaymentPending, $"Payment for order {o.OrderId} still pending after 30 s")
         .Wait(1000, 2000).Warning(Services.Payment, EventIds.PaymentRetry, $"Retrying payment for order {o.OrderId} (attempt 2/2)")
         .Wait(30000).Error(Services.Payment, EventIds.GatewayTimeout, $"Payment gateway request timed out for order {o.OrderId}", TimeoutException, 30000)
         .Wait(5, 20).Error(Services.Payment, EventIds.PaymentFailed, $"Payment failed for order {o.OrderId}: gateway retries exhausted")
         .Wait(5, 30).Error(Services.Order, EventIds.OrderPaymentFailed, $"Order {o.OrderId} marked as PaymentFailed");
        CommonFlows.ReleaseInventory(f);
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at) =>
        g.Emit(at, Levels.Information, Services.Payment, EventIds.GatewayLatencyRecovered,
            $"Payment gateway {Gateway} latency back to normal: p95 {g.Rng.Between(350, 700)} ms");

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng) =>
        (service, metric) switch
        {
            (Services.Payment, "payment_gateway_latency_p95_ms") => Latency(incident, at, rng),
            _ => null
        };
}
