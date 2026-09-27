using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// The external payment gateway slows down. The variants differ in how that reaches the customer:
/// <code>
/// timeout-order-failure  latency → gateway timeouts → retries exhausted → order PaymentFailed
/// retry-amplification    latency → PaymentService retries (4 s attempts) → outbound call rate multiplies
///                          → thread pool starvation → OrderService's 10 s calls time out → orders fail
/// late-authorization     latency → OrderService's 10 s call times out → order failed
///                          → the gateway authorizes the charge anyway → paid-but-failed orders
/// </code>
/// (Latency pinning database connections until the pool runs out is <see cref="GatewaySlowdownCascadeScenario"/>.)
/// </summary>
internal sealed class PaymentGatewayTimeoutScenario : IncidentScenarioBase
{
    private const string Gateway = "api.paygate-sim.example";
    private const int OrderTimeoutMs = 10000;
    private const int AttemptTimeoutMs = 4000;
    private const string StageKey = "stage";
    private const string RetryAmplification = "retry-amplification";
    private const string LateAuthorization = "late-authorization";

    private static readonly string ShortGatewayTimeout =
        $"System.TimeoutException: The operation has timed out after {AttemptTimeoutMs} ms.\n" +
        "   at PaymentService.Gateway.PayGateClient.AuthorizeAsync(PaymentRequest request, CancellationToken ct)\n" +
        "   at PaymentService.Payments.RetryingPaymentProcessor.AuthorizeAsync(Order order, CancellationToken ct)";

    private const string CallerDisconnected =
        "System.OperationCanceledException: The operation was canceled.\n" +
        "   at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpProtocol.ThrowIfRequestAborted()\n" +
        "   at PaymentService.Payments.PaymentsController.Charge(ChargeRequest request, CancellationToken ct)";

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
    public override IReadOnlyList<CausalLink> CausalChain =>
    [
        new(Services.Payment, EventIds.GatewayLatencyHigh, "Gateway latency rises"),
        new(Services.Payment, EventIds.GatewayTimeout, "Gateway calls time out"),
        new(Services.Payment, EventIds.PaymentFailed, "Retries exhausted, payments fail"),
        new(Services.Order, EventIds.OrderPaymentFailed, "Orders marked PaymentFailed"),
    ];
    public override IReadOnlyList<EvidenceHint> RecoveryEvidence =>
    [
        new(Services.Payment, EventIds.GatewayLatencyRecovered, "Gateway latency back to normal"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Check the payment gateway status and latency",
        "Fail over to a secondary payment provider",
        "Add a circuit breaker and review timeout/retry policy for gateway calls",
    ];

    public override IReadOnlyList<ScenarioVariant> Variants =>
    [
        new("timeout-order-failure", IncidentShape.Direct),
        new(RetryAmplification, IncidentShape.Cascade)
        {
            RootCause =
                $"The external payment gateway ({Gateway}) slowed down. PaymentService retries every gateway call that exceeds its {AttemptTimeoutMs / 1000} s " +
                "attempt timeout with no backoff or retry budget, multiplying outbound calls; the extra in-flight work starved PaymentService's thread pool, " +
                $"so payment requests queued past OrderService's {OrderTimeoutMs / 1000} s timeout and orders failed, even for payments the gateway would have answered quickly.",
            ExpectedEvidence =
            [
                new(Services.Payment, EventIds.GatewayLatencyHigh, "Gateway p95 latency rises first"),
                new(Services.Payment, EventIds.PaymentRetry, $"Payments retried after {AttemptTimeoutMs / 1000} s gateway attempt timeouts"),
                new(Services.Payment, EventIds.GatewayCallRateHigh, "Outbound gateway calls several times the inbound payment rate"),
                new(Services.Payment, EventIds.PaymentThreadPoolStarvation, "Thread pool starvation on the PaymentService instances"),
                new(Services.Order, EventIds.PaymentCallTimeout, "OrderService's calls to PaymentService time out (no socket error)"),
                new(Services.Order, EventIds.OrderPaymentFailed, "Orders marked PaymentFailed"),
            ],
            CausalChain =
            [
                new(Services.Payment, EventIds.GatewayLatencyHigh, "Gateway latency rises"),
                new(Services.Payment, EventIds.GatewayTimeout, $"Gateway attempts exceed PaymentService's {AttemptTimeoutMs / 1000} s attempt timeout", Concurrent: true),
                new(Services.Payment, EventIds.PaymentRetry, "Every timed-out attempt is retried immediately", Concurrent: true),
                new(Services.Payment, EventIds.GatewayCallRateHigh, "Retries multiply the outbound call rate"),
                new(Services.Payment, EventIds.PaymentThreadPoolStarvation, "In-flight calls starve PaymentService's thread pool"),
                new(Services.Order, EventIds.PaymentCallTimeout, "Queued payment requests exceed OrderService's timeout", Concurrent: true),
                new(Services.Order, EventIds.OrderPaymentFailed, "Orders marked PaymentFailed", Concurrent: true),
            ],
            Remediation =
            [
                "Add exponential backoff, jitter and a retry budget to gateway calls",
                "Add a circuit breaker on the gateway and bulkhead gateway calls away from the request thread pool",
                "Escalate to the payment gateway provider or fail over to a secondary provider",
            ],
        },
        new(LateAuthorization, IncidentShape.Correlated)
        {
            RootCause =
                $"The external payment gateway ({Gateway}) slowed to 11-25 s per authorization. OrderService stops waiting for PaymentService after " +
                $"{OrderTimeoutMs / 1000} s and fails the order, but PaymentService keeps waiting and the gateway authorizes the charge anyway, " +
                "so customers are charged for orders marked PaymentFailed.",
            ExpectedEvidence =
            [
                new(Services.Payment, EventIds.GatewayLatencyHigh, "Gateway p95 latency far above OrderService's timeout"),
                new(Services.Order, EventIds.PaymentCallTimeout, "OrderService's calls to PaymentService time out (no socket error: the requests reached it)"),
                new(Services.Order, EventIds.OrderPaymentFailed, "Orders marked PaymentFailed"),
                new(Services.Order, EventIds.LatePaymentForFailedOrder, "Authorizations arrive after the order already failed"),
            ],
            CausalChain =
            [
                new(Services.Payment, EventIds.GatewayLatencyHigh, "Gateway latency rises above OrderService's timeout"),
                new(Services.Order, EventIds.PaymentCallTimeout, "OrderService gives up on PaymentService"),
                new(Services.Order, EventIds.OrderPaymentFailed, "Orders marked PaymentFailed"),
                new(Services.Order, EventIds.LatePaymentForFailedOrder, "The gateway authorizes the charge after the order failed"),
            ],
            Remediation =
            [
                "Refund or void payments authorized for failed orders",
                "Make PaymentService's gateway timeout shorter than OrderService's, or cancel the authorization when the caller gives up",
                "Use an idempotent, asynchronous payment status instead of a synchronous call with a timeout",
            ],
        },
    ];

    private static int Latency(ActiveIncident incident, DateTime at, Random rng) => incident.Variant.Name switch
    {
        RetryAmplification => rng.Between(4500, 9000),
        LateAuthorization => rng.Between(14000, 25000),
        _ => (int)(4000 + 26000 * Math.Min(1, incident.Progress(at) * 2)) + rng.Between(-500, 500),
    };

    private static int Stage(ActiveIncident incident) => incident.State.TryGetValue(StageKey, out var s) ? int.Parse(s) : 0;

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at) => ReportLatency(g, incident, at);

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        ReportLatency(g, incident, at);
        if (incident.Variant.Name != RetryAmplification) return;

        // Stages are announced before requests can show their effects (RunOrder reads the announced stage).
        var progress = incident.Progress(at);
        if (Stage(incident) == 0 && progress >= 0.25)
        {
            incident.State[StageKey] = "1";
            ReportCallRate(g, at);
        }
        else if (Stage(incident) == 1 && progress >= 0.45)
        {
            incident.State[StageKey] = "2";
            foreach (var host in Services.HostsFor(Services.Payment))
                ReportStarvation(g, at, host);
            return;
        }

        if (Stage(incident) >= 1) ReportCallRate(g, at.AddMilliseconds(g.Rng.Between(200, 1500)));
        if (Stage(incident) >= 2)
            foreach (var host in Services.HostsFor(Services.Payment))
                ReportStarvation(g, at.AddMilliseconds(g.Rng.Between(300, 2500)), host);
    }

    private static void ReportLatency(LogGenerator g, ActiveIncident incident, DateTime at) =>
        g.Emit(at, Levels.Warning, Services.Payment, EventIds.GatewayLatencyHigh,
            $"Payment gateway {Gateway} latency above threshold: p95 {Latency(incident, at, g.Rng)} ms (threshold 2000 ms)");

    private static void ReportCallRate(LogGenerator g, DateTime at) =>
        g.Emit(at, Levels.Warning, Services.Payment, EventIds.GatewayCallRateHigh,
            $"Outbound calls to {Gateway}: {g.Rng.Between(40, 75)} req/s, {g.Rng.Between(2.6, 3.9):F1}x the inbound payment request rate");

    private static void ReportStarvation(LogGenerator g, DateTime at, string host) =>
        g.Emit(at, Levels.Warning, Services.Payment, EventIds.PaymentThreadPoolStarvation,
            $"ThreadPool starvation detected: {g.Rng.Between(250, 700)} work items queued, {g.Rng.Between(150, 220)} threads busy", host: host);

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        switch (incident.Variant.Name)
        {
            case RetryAmplification: RunRetryAmplification(f, incident); return;
            case LateAuthorization: RunLateAuthorization(f, incident); return;
        }

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
        var firstAttempt = f.Cursor;
        f.Wait(5000).Warning(Services.Payment, EventIds.GatewayResponseDelayed, $"Payment gateway response delayed for order {o.OrderId} (5000 ms elapsed)")
         .Wait(25000).Error(Services.Payment, EventIds.GatewayTimeout, $"Payment gateway request timed out for order {o.OrderId}", Exceptions.GatewayTimeout, 30000,
            f.TimedOut("PayGateClient.Authorize", firstAttempt, 1))
         .Wait(5, 30).Warning(Services.Order, EventIds.PaymentPending, $"Payment for order {o.OrderId} still pending after 30 s")
         .Wait(1000, 2000).Warning(Services.Payment, EventIds.PaymentRetry, $"Retrying payment for order {o.OrderId} (attempt 2/2)");
        var secondAttempt = f.Cursor;
        f.Wait(30000).Error(Services.Payment, EventIds.GatewayTimeout, $"Payment gateway request timed out for order {o.OrderId}", Exceptions.GatewayTimeout, 30000,
            f.TimedOut("PayGateClient.Authorize", secondAttempt, 2))
         .Wait(5, 20).Error(Services.Payment, EventIds.PaymentFailed, $"Payment failed for order {o.OrderId}: gateway retries exhausted")
         .Wait(5, 30).Error(Services.Order, EventIds.OrderPaymentFailed, $"Order {o.OrderId} marked as PaymentFailed");
        CommonFlows.ReleaseInventory(f);
    }

    /// <summary>
    /// Before starvation: slow first attempts time out after 4 s and are retried, but the payment still completes inside
    /// OrderService's timeout. After: requests queue for a thread first, so OrderService gives up on most of them.
    /// </summary>
    private static void RunRetryAmplification(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        var starved = Stage(incident) >= 2;
        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);
        if (!CommonFlows.ReserveInventory(f)) return;
        CommonFlows.StartPayment(f);
        var paymentStart = f.Cursor;

        // Either well past OrderService's timeout, or short enough for the slowest retry to still fit inside it.
        var queuedMs = starved ? (f.Rng.Chance(0.85) ? f.Rng.Between(OrderTimeoutMs + 500, 16000) : f.Rng.Between(800, 2400)) : 0;
        if (queuedMs + AttemptTimeoutMs + 3500 < OrderTimeoutMs)
        {
            f.Wait(queuedMs);
            AuthorizeWithRetry(f, incident);
            CommonFlows.ConfirmAndNotify(f);
            return;
        }

        // OrderService gives up; PaymentService only notices when the queued request finally gets a thread.
        incident.MarkAffected(o);
        var callerTimeoutAt = paymentStart.AddMilliseconds(OrderTimeoutMs);
        f.Wait(OrderTimeoutMs).Error(Services.Order, EventIds.PaymentCallTimeout, $"HTTP request to PaymentService timed out for order {o.OrderId}",
                Exceptions.HttpClientTimeout("PaymentClient", "ChargeAsync", OrderTimeoutMs / 1000), OrderTimeoutMs, f.TimedOut("PaymentClient.Charge", paymentStart, 1))
         .Wait(5, 30).Error(Services.Order, EventIds.OrderPaymentFailed, $"Order {o.OrderId} marked as PaymentFailed");
        CommonFlows.ReleaseInventory(f);

        f.At(paymentStart).Wait(queuedMs)
         .Warning(Services.Payment, EventIds.PaymentFailed, $"Payment for order {o.OrderId} abandoned after {queuedMs} ms in the request queue: caller disconnected",
            CallerDisconnected, queuedMs, Flow.Late("PaymentClient.Charge", paymentStart, callerTimeoutAt, success: false, 1));
    }

    private static void AuthorizeWithRetry(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        var latency = f.Rng.Chance(0.6) ? f.Rng.Between(AttemptTimeoutMs + 500, 9000) : f.Rng.Between(600, 3500);
        if (latency > AttemptTimeoutMs)
        {
            var attemptStart = f.Cursor;
            f.Wait(AttemptTimeoutMs).Warning(Services.Payment, EventIds.GatewayTimeout, $"Payment gateway attempt timed out for order {o.OrderId} after {AttemptTimeoutMs} ms",
                    ShortGatewayTimeout, AttemptTimeoutMs, f.TimedOut("PayGateClient.Authorize", attemptStart, 1))
             .Wait(0, 20).Warning(Services.Payment, EventIds.PaymentRetry, $"Retrying payment for order {o.OrderId} (attempt 2/3)");
            latency = f.Rng.Between(600, 3500);
        }
        f.Wait(latency).Info(Services.Payment, EventIds.PaymentAuthorized, $"Payment authorized for order {o.OrderId} (transaction txn_{f.Rng.Hex(10)})", latency);
    }

    private static void RunLateAuthorization(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);
        if (!CommonFlows.ReserveInventory(f)) return;
        CommonFlows.StartPayment(f);
        var paymentStart = f.Cursor;

        var latency = f.Rng.Chance(0.75) ? f.Rng.Between(11000, 25000) : f.Rng.Between(1500, 9000);
        if (latency < OrderTimeoutMs)
        {
            Authorize(f, latency);
            CommonFlows.ConfirmAndNotify(f);
            return;
        }

        incident.MarkAffected(o);
        var callerTimeoutAt = paymentStart.AddMilliseconds(OrderTimeoutMs);
        f.Wait(OrderTimeoutMs).Error(Services.Order, EventIds.PaymentCallTimeout, $"HTTP request to PaymentService timed out for order {o.OrderId}",
                Exceptions.HttpClientTimeout("PaymentClient", "ChargeAsync", OrderTimeoutMs / 1000), OrderTimeoutMs, f.TimedOut("PaymentClient.Charge", paymentStart, 1))
         .Wait(5, 30).Error(Services.Order, EventIds.OrderPaymentFailed, $"Order {o.OrderId} marked as PaymentFailed");
        CommonFlows.ReleaseInventory(f);
        var orderFailedAt = f.Cursor;

        f.At(paymentStart);
        Authorize(f, latency, Flow.Late("PaymentClient.Charge", paymentStart, callerTimeoutAt, success: true, 1));
        if (f.Cursor < orderFailedAt) f.At(orderFailedAt);
        f.Wait(20, 200).Warning(Services.Order, EventIds.LatePaymentForFailedOrder,
            $"Payment authorization for order {o.OrderId} arrived after the order was marked PaymentFailed; refund required");
    }

    private static void Authorize(Flow f, int latencyMs, OperationInfo? operation = null)
    {
        var o = f.Order;
        if (latencyMs > 5000)
            f.Wait(5000).Warning(Services.Payment, EventIds.GatewayResponseDelayed, $"Payment gateway response delayed for order {o.OrderId} (5000 ms elapsed)")
             .Wait(latencyMs - 5000);
        else
            f.Wait(latencyMs);
        f.Info(Services.Payment, EventIds.PaymentAuthorized, $"Payment authorized for order {o.OrderId} (transaction txn_{f.Rng.Hex(10)})", latencyMs, operation);
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at) =>
        g.Emit(at, Levels.Information, Services.Payment, EventIds.GatewayLatencyRecovered,
            $"Payment gateway {Gateway} latency back to normal: p95 {g.Rng.Between(350, 700)} ms");

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng) =>
        (service, metric) switch
        {
            (Services.Payment, "payment_gateway_latency_p95_ms") => Latency(incident, at, rng),
            (Services.Payment, "cpu_percent") when incident.Variant.Name == RetryAmplification && Stage(incident) >= 2 => rng.Between(88.0, 99.0),
            (Services.Order, "upstream_payment_latency_p95_ms")
                when incident.Variant.Name == LateAuthorization || (incident.Variant.Name == RetryAmplification && Stage(incident) >= 2) => OrderTimeoutMs,
            _ => null
        };
}
