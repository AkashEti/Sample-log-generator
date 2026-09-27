using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// <code>
/// [0] external gateway slows down (PaymentService)
///  └► [1] each payment holds a DB connection while waiting on the gateway → pool usage climbs;
///         calls exceed OrderService's 10 s timeout → OrderService gives up, some payments authorize after the order failed
///      └► [2] pool exhausted → every payment fails, even ones that never reach the gateway
///           └► OrderService timeouts + retries → orders PaymentFailed
/// </code>
/// It looks like <see cref="DatabaseConnectionPoolExhaustedScenario"/> (pool exhaustion), but the gateway latency
/// comes first, there is no deploy, and connections are held only as long as a gateway call takes.
/// </summary>
internal sealed class GatewaySlowdownCascadeScenario : CascadeScenarioBase
{
    private const string Gateway = "api.paygate-sim.example";
    private const string HoldingCaller = "PaymentProcessor.AuthorizeAsync";
    private const int MaxPoolSize = 100;
    private const int OrderTimeoutMs = 10000;

    public override IncidentScenario Scenario => IncidentScenario.GatewaySlowdownCascade;
    public override string Title => "Payment gateway slowdown cascading into connection pool exhaustion";
    public override string RootCauseService => Services.Payment;
    public override string RootCause =>
        $"The external payment gateway ({Gateway}) slowed down. PaymentService holds a payments-db connection while it waits for the gateway " +
        $"({HoldingCaller}), so slow calls pinned connections until the pool (max {MaxPoolSize}) was exhausted. From then on every payment failed, " +
        $"including ones that never reached the gateway, and OrderService's {OrderTimeoutMs / 1000} s calls to PaymentService timed out, so orders failed.";
    public override IReadOnlyList<string> AffectedServices => [Services.Payment, Services.Order, Services.Inventory];
    protected override IReadOnlyList<double> StageStarts => [0, 0.2, 0.45];
    protected override int ContainedAt => 1;
    protected override string ContainedRootCause =>
        $"The external payment gateway ({Gateway}) slowed down and PaymentService held a payments-db connection for the whole gateway call " +
        $"({HoldingCaller}), so pool usage climbed but the pool never ran out. Payments slower than OrderService's {OrderTimeoutMs / 1000} s timeout " +
        "failed their orders, and some of those payments were still authorized afterwards.";
    public override IReadOnlyList<CausalLink> CausalChain =>
    [
        new(Services.Payment, EventIds.GatewayLatencyHigh, "Gateway p95 latency rises first; there is no deploy or other change before it"),
        new(Services.Payment, EventIds.ConnectionHeldTooLong, $"Connections held only as long as a gateway call takes (opened by {HoldingCaller})"),
        new(Services.Payment, EventIds.ConnectionPoolUsageHigh, "Pool usage climbs on every instance"),
        new(Services.Order, EventIds.PaymentCallTimeout, "OrderService's calls to PaymentService time out (no socket error: requests did reach PaymentService)", Concurrent: true),
        new(Services.Order, EventIds.OrderPaymentFailed, "Orders marked PaymentFailed", Concurrent: true),
        new(Services.Payment, EventIds.ConnectionPoolExhausted, "Pool exhausted"),
        new(Services.Payment, EventIds.PaymentPersistFailed, "Every payment now fails waiting for a pool connection"),
    ];
    // A side effect whose timing varies, so it is expected evidence but not a link in the ordered chain.
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        .. CausalChain.Select(l => l.ToHint()),
        new(Services.Order, EventIds.LatePaymentForFailedOrder, "Payments authorized after OrderService already failed the order"),
    ];

    public override IReadOnlyList<EvidenceHint> RecoveryEvidence =>
    [
        new(Services.Payment, EventIds.GatewayLatencyRecovered, "Gateway latency back to normal"),
        new(Services.Payment, EventIds.ConnectionPoolRecovered, "Connection pool usage back to normal"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Do not hold a DB connection or transaction across the external gateway call",
        "Add a circuit breaker and a timeout on gateway calls shorter than OrderService's timeout",
        "Bulkhead: cap concurrent gateway calls so they cannot drain the connection pool",
        "Refund payments authorized for orders that had already failed",
        "Escalate to the payment gateway provider or fail over to a secondary provider",
    ];

    private static int GatewayLatency(int stage, Random rng) => stage switch
    {
        0 => rng.Between(3000, 8000),
        1 => rng.Between(12000, 28000),
        _ => 30000,
    };

    private static int PoolUsage(int stage, ActiveIncident incident, DateTime at, Random rng) => stage switch
    {
        0 => rng.Between(15, 30),
        1 => Math.Clamp(45 + (int)(50 * (incident.Progress(at) - 0.2) / 0.25) + rng.Between(-3, 3), 45, 97),
        _ => MaxPoolSize,
    };

    // Each stage's defining event is logged exactly when the stage begins, before any request can show its effects.
    protected override void OnStageReached(LogGenerator g, ActiveIncident incident, int stage, DateTime at)
    {
        switch (stage)
        {
            case 0:
                g.Emit(at, Levels.Warning, Services.Payment, EventIds.GatewayLatencyHigh,
                    $"Payment gateway {Gateway} latency above threshold: p95 {GatewayLatency(0, g.Rng)} ms (threshold 2000 ms)");
                break;
            case 1:
                g.Emit(at, Levels.Warning, Services.Payment, EventIds.ConnectionHeldTooLong,
                    $"Connection to payments-db held for {g.Rng.Between(12, 20)} s without being returned to the pool (opened by {HoldingCaller})");
                break;
            case 2:
                g.Emit(at, Levels.Error, Services.Payment, EventIds.ConnectionPoolExhausted,
                    $"Connection pool exhausted on payments-db: {MaxPoolSize}/{MaxPoolSize} connections in use, {g.Rng.Between(40, 120)} requests waiting");
                break;
        }
    }

    protected override void OnSymptoms(LogGenerator g, ActiveIncident incident, int stage, DateTime at)
    {
        var latency = GatewayLatency(stage, g.Rng);
        g.Emit(at, Levels.Warning, Services.Payment, EventIds.GatewayLatencyHigh,
            $"Payment gateway {Gateway} latency above threshold: p95 {latency} ms (threshold 2000 ms)");
        if (stage == 0) return;

        // Held about as long as a gateway call takes (unlike a leak, where connections are never returned).
        g.Emit(at, Levels.Warning, Services.Payment, EventIds.ConnectionHeldTooLong,
            $"Connection to payments-db held for {Math.Max(10, latency / 1000 - g.Rng.Between(0, 3))} s without being returned to the pool (opened by {HoldingCaller})");

        foreach (var host in Services.HostsFor(Services.Payment))
        {
            var hostAt = at.AddMilliseconds(g.Rng.Between(300, 2000));
            var usage = PoolUsage(stage, incident, hostAt, g.Rng);
            if (usage >= MaxPoolSize)
                g.Emit(hostAt, Levels.Error, Services.Payment, EventIds.ConnectionPoolExhausted,
                    $"Connection pool exhausted on payments-db: {MaxPoolSize}/{MaxPoolSize} connections in use, {g.Rng.Between(40, 120)} requests waiting", host: host);
            else
                g.Emit(hostAt, Levels.Warning, Services.Payment, EventIds.ConnectionPoolUsageHigh,
                    $"Database connection pool usage high: {usage}/{MaxPoolSize} active connections (payments-db)", host: host);
        }
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        var stage = Stage(incident);
        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);
        if (!CommonFlows.ReserveInventory(f)) return;
        CommonFlows.StartPayment(f);
        var paymentStart = f.Cursor;

        switch (stage)
        {
            case 0:
            {
                // Slow, but still inside OrderService's timeout.
                var latency = f.Rng.Chance(0.7) ? GatewayLatency(0, f.Rng) : f.Rng.Between(300, 900);
                Authorize(f, latency);
                CommonFlows.ConfirmAndNotify(f);
                return;
            }
            case 1:
            {
                var latency = GatewayLatency(1, f.Rng) - f.Rng.Between(0, 6000);
                if (latency < OrderTimeoutMs)
                {
                    Authorize(f, latency);
                    CommonFlows.ConfirmAndNotify(f);
                    return;
                }

                // OrderService gives up at 10 s while PaymentService keeps waiting on the gateway.
                incident.MarkAffected(o);
                var callerTimeoutAt = paymentStart.AddMilliseconds(OrderTimeoutMs);
                f.Wait(OrderTimeoutMs).Error(Services.Order, EventIds.PaymentCallTimeout, $"HTTP request to PaymentService timed out for order {o.OrderId}",
                        Exceptions.HttpClientTimeout("PaymentClient", "ChargeAsync", OrderTimeoutMs / 1000), OrderTimeoutMs, f.TimedOut("PaymentClient.Charge", paymentStart, 1))
                 .Wait(5, 30).Error(Services.Order, EventIds.OrderPaymentFailed, $"Order {o.OrderId} marked as PaymentFailed");
                CommonFlows.ReleaseInventory(f);
                var orderFailedAt = f.Cursor;

                f.At(paymentStart);
                if (latency >= 28000)
                {
                    f.Wait(30000).Error(Services.Payment, EventIds.GatewayTimeout, $"Payment gateway request timed out for order {o.OrderId}", Exceptions.GatewayTimeout, 30000,
                        Flow.Late("PaymentClient.Charge", paymentStart, callerTimeoutAt, success: false, 1))
                     .Wait(5, 20).Error(Services.Payment, EventIds.PaymentFailed, $"Payment failed for order {o.OrderId}: gateway timeout");
                }
                else
                {
                    Authorize(f, latency, Flow.Late("PaymentClient.Charge", paymentStart, callerTimeoutAt, success: true, 1));
                    if (f.Cursor < orderFailedAt) f.At(orderFailedAt);
                    f.Wait(20, 200).Warning(Services.Order, EventIds.LatePaymentForFailedOrder,
                        $"Payment authorization for order {o.OrderId} arrived after the order was marked PaymentFailed; refund required");
                }
                return;
            }
            default:
            {
                if (f.Rng.Chance(0.05))
                {
                    Authorize(f, f.Rng.Between(2000, 6000));
                    CommonFlows.ConfirmAndNotify(f);
                    return;
                }

                // Pool exhausted: both attempts wait 15 s for a connection; OrderService gives up after 10 s each time.
                incident.MarkAffected(o);
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    var attemptStart = f.Cursor;
                    f.Wait(OrderTimeoutMs).Error(Services.Order, EventIds.PaymentCallTimeout, $"HTTP request to PaymentService timed out for order {o.OrderId}",
                        Exceptions.HttpClientTimeout("PaymentClient", "ChargeAsync", OrderTimeoutMs / 1000), OrderTimeoutMs,
                        f.TimedOut("PaymentClient.Charge", attemptStart, attempt));
                    var orderSideAt = f.Cursor;

                    f.At(attemptStart).Wait(15000)
                     .Error(Services.Payment, EventIds.PaymentPersistFailed, $"Failed to persist payment transaction for order {o.OrderId}", Exceptions.PoolTimeout, 15000,
                        Flow.Late("PaymentClient.Charge", attemptStart, orderSideAt, success: false, attempt));

                    f.At(orderSideAt);
                    if (attempt == 1)
                    {
                        f.Wait(500, 1500).Warning(Services.Order, EventIds.PaymentCallRetry, $"Retrying PaymentService call for order {o.OrderId} (attempt 2/2)");
                        CommonFlows.StartPayment(f);
                    }
                }

                f.Wait(5, 30).Error(Services.Order, EventIds.OrderPaymentFailed, $"Order {o.OrderId} marked as PaymentFailed");
                CommonFlows.ReleaseInventory(f);
                return;
            }
        }
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

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        g.Emit(at, Levels.Information, Services.Payment, EventIds.GatewayLatencyRecovered,
            $"Payment gateway {Gateway} latency back to normal: p95 {g.Rng.Between(350, 700)} ms");
        if (Stage(incident) == 0) return;
        foreach (var host in Services.HostsFor(Services.Payment))
            g.Emit(at.AddSeconds(g.Rng.Between(20, 40)), Levels.Information, Services.Payment, EventIds.ConnectionPoolRecovered,
                $"Database connection pool usage normal: {g.Rng.Between(8, 18)}/{MaxPoolSize} active connections (payments-db)", host: host);
    }

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng)
    {
        var stage = Stage(incident);
        return (service, metric) switch
        {
            (Services.Payment, "payment_gateway_latency_p95_ms") => GatewayLatency(stage, rng),
            (Services.Payment, "db_connections_active") => PoolUsage(stage, incident, at, rng),
            (Services.Payment, "db_connection_wait_ms") => stage >= 2 ? rng.Between(12000, 15000) : stage == 1 ? rng.Between(200, 2000) : null,
            (Services.Order, "upstream_payment_latency_p95_ms") => stage >= 1 ? OrderTimeoutMs : null,
            _ => null
        };
    }
}
