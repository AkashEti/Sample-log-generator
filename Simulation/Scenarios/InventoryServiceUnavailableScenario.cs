using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

internal sealed class InventoryServiceUnavailableScenario : IncidentScenarioBase
{
    private const string CircuitOpenKey = "circuitOpen";
    private const string FirstCrashKey = "firstCrashTicks";
    private const string OutOfMemory =
        "System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.\n" +
        "   at System.Collections.Generic.Dictionary`2.Resize(Int32 newSize, Boolean forceNewHashCodes)\n" +
        "   at InventoryService.Caching.StockCache.RebuildAsync(CancellationToken ct)";
    private const string ServiceUnavailable =
        "System.Net.Http.HttpRequestException: Response status code does not indicate success: 503 (Service Unavailable).\n" +
        "   at OrderService.Clients.InventoryClient.ReserveAsync(ReserveStockRequest request, CancellationToken ct)";
    private const string BrokenCircuit =
        "Polly.CircuitBreaker.BrokenCircuitException: The circuit is now open and is not allowing calls.\n" +
        "   at OrderService.Clients.InventoryClient.ReserveAsync(ReserveStockRequest request, CancellationToken ct)";

    public override IncidentScenario Scenario => IncidentScenario.InventoryServiceUnavailable;
    public override string Title => "InventoryService unavailable";
    public override IncidentDifficulty Difficulty => IncidentDifficulty.Direct;
    public override string RootCauseService => Services.Inventory;
    public override string RootCause =>
        "InventoryService instances crash with OutOfMemoryException while rebuilding the stock cache and are stuck in a crash loop; " +
        "the service returns 503 and OrderService's circuit breaker opens, rejecting orders.";
    public override IReadOnlyList<string> AffectedServices => [Services.Inventory, Services.Order];
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        new(Services.Inventory, EventIds.MemoryUsageHigh, "Memory near the 4 GB limit on each instance right before it crashes"),
        new(Services.Inventory, EventIds.ProcessCrashed, "OutOfMemoryException in StockCache.RebuildAsync"),
        new(Services.Inventory, EventIds.HealthCheckFailed, "Health checks returning 503"),
        new(Services.Order, EventIds.InventoryCircuitOpened, "Circuit breaker for InventoryService opened"),
        new(Services.Order, EventIds.OrderRejectedInventoryUnavailable, "Orders rejected because inventory is unavailable"),
    ];
    public override IReadOnlyList<EvidenceHint> CausalChain =>
    [
        new(Services.Inventory, EventIds.MemoryUsageHigh, "Memory reaches the limit during the stock cache rebuild"),
        new(Services.Inventory, EventIds.ProcessCrashed, "OutOfMemoryException crash loop"),
        new(Services.Order, EventIds.InventoryCallFailed, "Reservation calls fail with 503"),
        new(Services.Order, EventIds.OrderRejectedInventoryUnavailable, "Orders rejected"),
        new(Services.Order, EventIds.InventoryCircuitOpened, "OrderService opens its circuit breaker after repeated failures"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Increase InventoryService memory limit or scale out temporarily",
        "Make the stock cache rebuild incremental/paged instead of loading everything into memory",
        "Add memory usage alerts for InventoryService",
    ];
    public override TimeSpan BackgroundInterval => TimeSpan.FromSeconds(20);

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        var restart = 1;
        foreach (var host in Services.HostsFor(Services.Inventory))
        {
            var crashedAt = Crash(g, host, at, restart);
            incident.State.TryAdd(FirstCrashKey, crashedAt.Ticks.ToString());
            at = at.AddMilliseconds(g.Rng.Between(1500, 4000));
        }
    }

    /// <returns>When the process died.</returns>
    private static DateTime Crash(LogGenerator g, string host, DateTime at, int restartCount)
    {
        // Memory climbs on the instance just before it dies: host-specific evidence leading up to the crash.
        // (Never emit before `at`: in live mode earlier timestamps would already have been flushed.)
        g.Emit(at, Levels.Warning, Services.Inventory, EventIds.MemoryUsageHigh,
            $"Memory usage {g.Rng.Between(3850, 4050)} MB of 4096 MB limit during stock cache rebuild", host: host);
        at = at.AddMilliseconds(g.Rng.Between(2000, 6000));
        g.Emit(at, Levels.Critical, Services.Inventory, EventIds.ProcessCrashed, "Unhandled exception. Process terminating.", exception: OutOfMemory, host: host);
        g.Emit(at.AddMilliseconds(g.Rng.Between(500, 1500)), Levels.Warning, Services.Inventory, EventIds.InstanceRestarting,
            $"Instance {host} restarting (restart count {restartCount})", host: host);
        return at;
    }

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in Services.HostsFor(Services.Inventory))
        {
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 3000)), Levels.Error, Services.Inventory, EventIds.HealthCheckFailed,
                $"Health check failed for {host}: /health returned 503 (stock cache rebuild in progress)", host: host);
        }

        // The breaker trips after the failures it counts, so it is logged after this round of checks.
        if (!incident.State.ContainsKey(CircuitOpenKey))
        {
            incident.State[CircuitOpenKey] = "true";
            g.Emit(at.AddMilliseconds(g.Rng.Between(3100, 4000)), Levels.Warning, Services.Order, EventIds.InventoryCircuitOpened,
                "Circuit breaker for InventoryService opened after 5 consecutive failures");
        }

        // Crash loop: one instance dies again on every other check.
        if (incident.BackgroundTicks % 2 == 1)
            Crash(g, g.Rng.Pick(Services.HostsFor(Services.Inventory)), at.AddMilliseconds(g.Rng.Between(3000, 8000)), incident.BackgroundTicks / 2 + 2);
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        // Instances are still up until the first one runs out of memory.
        var firstCrash = new DateTime(long.Parse(incident.State[FirstCrashKey]), DateTimeKind.Utc);
        if (f.Cursor < firstCrash || !f.Rng.Chance(0.9))
        {
            CommonFlows.Normal(f);
            return;
        }

        var o = f.Order;
        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);
        incident.MarkAffected(o);

        if (incident.State.ContainsKey(CircuitOpenKey))
        {
            var ms = f.Rng.Between(1, 5);
            f.Wait(ms).Error(Services.Order, EventIds.InventoryCallFailed, $"Failed to reserve inventory for order {o.OrderId}", BrokenCircuit, ms);
        }
        else
        {
            var ms = f.Rng.Between(80, 3000);
            f.Wait(ms).Error(Services.Order, EventIds.InventoryCallFailed, $"Failed to reserve inventory for order {o.OrderId}", ServiceUnavailable, ms);
        }

        f.Wait(2, 10).Warning(Services.Order, EventIds.OrderRejectedInventoryUnavailable, $"Order {o.OrderId} rejected: inventory unavailable");
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in Services.HostsFor(Services.Inventory))
        {
            g.Emit(at, Levels.Information, Services.Inventory, EventIds.InstanceRecovered,
                $"Instance {host} healthy; stock cache rebuilt ({g.Rng.Between(45000, 52000)} SKUs)", host: host);
            at = at.AddMilliseconds(g.Rng.Between(500, 2000));
        }

        g.Emit(at.AddSeconds(5), Levels.Information, Services.Order, EventIds.InventoryCircuitClosed, "Circuit breaker for InventoryService closed");
    }

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng) =>
        (service, metric) switch
        {
            (Services.Inventory, "healthy_instances") => rng.Chance(0.2) ? 1 : 0,
            (Services.Inventory, "memory_mb") => rng.Between(3700, 4096),
            (Services.Inventory, "cpu_percent") => rng.Between(85.0, 99.0),
            _ => null
        };
}
