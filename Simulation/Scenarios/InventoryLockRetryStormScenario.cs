using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// <code>
/// [0] batch job locks the stock table (InventoryService) → reservations wait on the lock
///  └► [1] reservations exceed OrderService's 5 s timeout → OrderService retries (3 attempts);
///         the timed-out reservations still finish in the background
///      └► [2] retries multiply the load → request surge → InventoryService thread pool starvation
///           └► [3] health checks time out → instances pulled from the load balancer → 503s, circuit opens → orders rejected
/// </code>
/// The loudest symptoms (starvation, health checks, 503s) look like capacity or a crash; the origin is a lock.
/// </summary>
internal sealed class InventoryLockRetryStormScenario : CascadeScenarioBase
{
    private const string Warehouse = "WH-2";
    private const int OrderTimeoutMs = 5000;
    private const int MaxAttempts = 3;
    private const string BlockingPidKey = "blockingPid";
    private const string ServiceUnavailable =
        "System.Net.Http.HttpRequestException: Response status code does not indicate success: 503 (Service Unavailable).\n" +
        "   at OrderService.Clients.InventoryClient.ReserveAsync(ReserveStockRequest request, CancellationToken ct)";
    private const string BrokenCircuit =
        "Polly.CircuitBreaker.BrokenCircuitException: The circuit is now open and is not allowing calls.\n" +
        "   at OrderService.Clients.InventoryClient.ReserveAsync(ReserveStockRequest request, CancellationToken ct)";

    public override IncidentScenario Scenario => IncidentScenario.InventoryLockRetryStorm;
    public override string Title => "Stock table lock escalating into an InventoryService retry storm";
    public override string RootCauseService => Services.Inventory;
    public override string RootCause =>
        $"InventoryService's stock recount job for warehouse {Warehouse} holds locks on the stock_levels table. Reservations block on the lock and " +
        $"exceed OrderService's {OrderTimeoutMs / 1000} s timeout; OrderService retries each call up to {MaxAttempts} times with no backoff or retry budget, " +
        "multiplying the load until InventoryService's thread pool starves, health checks time out, instances are pulled and orders are rejected.";
    public override IReadOnlyList<string> AffectedServices => [Services.Inventory, Services.Order];
    protected override IReadOnlyList<double> StageStarts => [0, 0.15, 0.35, 0.6];
    public override IReadOnlyList<EvidenceHint> CausalChain =>
    [
        new(Services.Inventory, EventIds.StockRecountStarted, $"Stock recount job for {Warehouse} starts right before the slowdown"),
        new(Services.Inventory, EventIds.LockWait, "Reservation queries wait on locks held by the recount job's UPDATE"),
        new(Services.Order, EventIds.InventoryCallTimeout, "OrderService's calls to InventoryService time out after 5 s"),
        new(Services.Order, EventIds.InventoryCallRetry, "OrderService retries every timed-out call"),
        new(Services.Inventory, EventIds.RequestSurge, "Inbound request rate several times normal (retries), not real traffic growth"),
        new(Services.Inventory, EventIds.ThreadPoolStarvation, "Thread pool starvation on InventoryService"),
        new(Services.Inventory, EventIds.HealthCheckTimeout, "Health checks time out, so instances are pulled"),
        new(Services.Order, EventIds.InventoryCallFailed, "Fast 503 / open-circuit failures: InventoryService is effectively down"),
    ];
    // Rejections after exhausted retries happen alongside the surge (both are effects of the retries), so they are
    // expected evidence rather than a link in the ordered chain.
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        .. CausalChain,
        new(Services.Order, EventIds.OrderRejectedInventoryUnavailable, "Orders rejected once all retries time out"),
    ];

    public override IReadOnlyList<string> Remediation =>
    [
        "Stop the stock recount job, then reschedule it off-peak with small batches and short transactions",
        "Add a retry budget, exponential backoff with jitter, and a circuit breaker to OrderService's inventory client",
        "Serve health checks from a separate thread pool/bulkhead so they are not starved by request load",
    ];

    private static int LockWaitMs(int stage, Random rng) => stage == 0 ? rng.Between(800, 3000) : rng.Between(4000, 12000);

    protected override void OnStageReached(LogGenerator g, ActiveIncident incident, int stage, DateTime at)
    {
        switch (stage)
        {
            case 0:
                incident.State[BlockingPidKey] = g.Rng.Between(3000, 9000).ToString();
                g.Emit(at, Levels.Information, Services.Inventory, EventIds.StockRecountStarted,
                    $"Stock recount job started for warehouse {Warehouse} ({g.Rng.Between(45000, 52000)} SKUs)", host: Services.HostsFor(Services.Inventory)[0]);
                break;
            case 2:
                g.Emit(at, Levels.Warning, Services.Inventory, EventIds.RequestSurge,
                    $"Inbound request rate {g.Rng.Between(55, 90)} req/s is {g.Rng.Between(3.2, 5.5):F1}x the 1-hour average");
                break;
            case 3:
                g.Emit(at, Levels.Error, Services.Inventory, EventIds.HealthCheckTimeout,
                    $"Health check for {Services.HostsFor(Services.Inventory)[0]} timed out after 2000 ms; instance removed from load balancer",
                    host: Services.HostsFor(Services.Inventory)[0]);
                g.Emit(at.AddMilliseconds(g.Rng.Between(500, 2000)), Levels.Warning, Services.Order, EventIds.InventoryCircuitOpened,
                    "Circuit breaker for InventoryService opened after 5 consecutive failures");
                break;
        }
    }

    protected override void OnSymptoms(LogGenerator g, ActiveIncident incident, int stage, DateTime at)
    {
        var pid = incident.State[BlockingPidKey];
        g.Emit(at, Levels.Warning, Services.Inventory, EventIds.LockWait,
            $"Lock wait on table stock_levels: reservation query waited {LockWaitMs(stage, g.Rng)} ms " +
            $"(blocked by pid {pid}: UPDATE stock_levels SET counted_qty = @qty WHERE warehouse_id = '{Warehouse}' AND sku BETWEEN @from AND @to)");

        if (stage >= 2)
        {
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 500)), Levels.Warning, Services.Inventory, EventIds.RequestSurge,
                $"Inbound request rate {g.Rng.Between(55, 90)} req/s is {g.Rng.Between(3.2, 5.5):F1}x the 1-hour average");
            foreach (var host in Services.HostsFor(Services.Inventory))
                g.Emit(at.AddMilliseconds(g.Rng.Between(600, 2500)), Levels.Warning, Services.Inventory, EventIds.ThreadPoolStarvation,
                    $"ThreadPool starvation detected: {g.Rng.Between(300, 900)} work items queued, {g.Rng.Between(180, 250)} threads busy", host: host);
        }

        if (stage >= 3)
        {
            foreach (var host in Services.HostsFor(Services.Inventory))
                g.Emit(at.AddMilliseconds(g.Rng.Between(0, 3000)), Levels.Error, Services.Inventory, EventIds.HealthCheckTimeout,
                    $"Health check for {host} timed out after 2000 ms; instance removed from load balancer", host: host);
        }
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        var stage = Stage(incident);
        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);

        if (stage == 0)
        {
            // Slow reservations, still inside the timeout.
            var ms = f.Rng.Between(1000, 4000);
            f.Wait(ms).Info(Services.Inventory, EventIds.StockReserved, $"Reserved {o.ItemCount} items for order {o.OrderId}", ms);
            if (CommonFlows.ProcessPayment(f)) CommonFlows.ConfirmAndNotify(f);
            return;
        }

        if (stage == 3 && f.Rng.Chance(0.9))
        {
            incident.MarkAffected(o);
            var ms = f.Rng.Between(1, 40);
            f.Wait(ms).Error(Services.Order, EventIds.InventoryCallFailed, $"Failed to reserve inventory for order {o.OrderId}",
                f.Rng.Chance(0.5) ? BrokenCircuit : ServiceUnavailable, ms);
            f.Wait(2, 10).Warning(Services.Order, EventIds.OrderRejectedInventoryUnavailable, $"Order {o.OrderId} rejected: inventory unavailable");
            return;
        }

        // Stages 1-2: each attempt that exceeds the timeout still finishes on InventoryService after OrderService gave up.
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var attemptStart = f.Cursor;
            var latency = stage == 1 ? f.Rng.Between(2500, 9000) : f.Rng.Between(6000, 16000);
            if (latency < OrderTimeoutMs)
            {
                f.Wait(latency).Info(Services.Inventory, EventIds.StockReserved, $"Reserved {o.ItemCount} items for order {o.OrderId}", latency);
                if (CommonFlows.ProcessPayment(f)) CommonFlows.ConfirmAndNotify(f);
                return;
            }

            incident.MarkAffected(o);
            f.Wait(latency).Info(Services.Inventory, EventIds.StockReserved, $"Reserved {o.ItemCount} items for order {o.OrderId}", latency);
            f.At(attemptStart).Wait(OrderTimeoutMs)
             .Error(Services.Order, EventIds.InventoryCallTimeout, $"HTTP request to InventoryService timed out for order {o.OrderId} (attempt {attempt}/{MaxAttempts})",
                Exceptions.HttpClientTimeout("InventoryClient", "ReserveAsync", OrderTimeoutMs / 1000), OrderTimeoutMs);
            if (attempt < MaxAttempts)
                f.Wait(0, 50).Warning(Services.Order, EventIds.InventoryCallRetry, $"Retrying InventoryService call for order {o.OrderId} (attempt {attempt + 1}/{MaxAttempts})");
        }

        f.Wait(5, 20).Warning(Services.Order, EventIds.OrderRejectedInventoryUnavailable, $"Order {o.OrderId} rejected: inventory unavailable");
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        g.Emit(at, Levels.Information, Services.Inventory, EventIds.StockRecountCompleted,
            $"Stock recount job for warehouse {Warehouse} completed in {Math.Max(1, (int)(at - incident.StartedAt).TotalMinutes)} min", host: Services.HostsFor(Services.Inventory)[0]);
        if (Stage(incident) < 3) return;

        foreach (var host in Services.HostsFor(Services.Inventory))
        {
            at = at.AddMilliseconds(g.Rng.Between(2000, 6000));
            g.Emit(at, Levels.Information, Services.Inventory, EventIds.InstanceRecovered, $"Instance {host} healthy; added back to load balancer", host: host);
        }
        g.Emit(at.AddSeconds(5), Levels.Information, Services.Order, EventIds.InventoryCircuitClosed, "Circuit breaker for InventoryService closed");
    }

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng)
    {
        if (service != Services.Inventory) return null;
        var stage = Stage(incident);
        return metric switch
        {
            "db_lock_wait_ms" => LockWaitMs(stage, rng),
            "threadpool_queue_length" => stage >= 2 ? rng.Between(300, 900) : null,
            "cpu_percent" => stage >= 2 ? rng.Between(85.0, 99.0) : null,
            "healthy_instances" => stage >= 3 ? rng.Between(0, 1) : null,
            _ => null
        };
    }
}
