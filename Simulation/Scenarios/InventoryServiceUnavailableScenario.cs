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
    public override IReadOnlyList<CausalLink> CausalChain =>
    [
        new(Services.Inventory, EventIds.MemoryUsageHigh, "Memory reaches the limit during the stock cache rebuild"),
        new(Services.Inventory, EventIds.ProcessCrashed, "OutOfMemoryException crash loop"),
        new(Services.Order, EventIds.InventoryCallFailed, "Reservation calls fail with 503"),
        new(Services.Order, EventIds.OrderRejectedInventoryUnavailable, "Orders rejected"),
        new(Services.Order, EventIds.InventoryCircuitOpened, "OrderService opens its circuit breaker after repeated failures"),
    ];
    public override IReadOnlyList<EvidenceHint> RecoveryEvidence =>
    [
        new(Services.Inventory, EventIds.InstanceRecovered, "Instances healthy again"),
        new(Services.Order, EventIds.InventoryCircuitClosed, "Circuit breaker closed"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Increase InventoryService memory limit or scale out temporarily",
        "Make the stock cache rebuild incremental/paged instead of loading everything into memory",
        "Add memory usage alerts for InventoryService",
    ];
    public override TimeSpan BackgroundInterval => TimeSpan.FromSeconds(20);

    private static IReadOnlyList<string> InventoryHosts => Services.HostsFor(Services.Inventory);

    /// <summary>All instances crash-looping (the circuit opens), or a single one (a third of reservations fail, the circuit stays closed).</summary>
    public override IReadOnlyList<ScenarioVariant> Variants =>
    [
        new("all-instances", IncidentShape.Direct),
        .. InventoryHosts.Select(SingleInstance),
    ];

    private static ScenarioVariant SingleInstance(string host)
    {
        var others = InventoryHosts.Where(h => h != host).ToList();
        return new ScenarioVariant($"single-instance:{host}", IncidentShape.Direct)
        {
            Hosts = [host],
            RootCause =
                $"InventoryService instance {host} crashes with OutOfMemoryException while rebuilding the stock cache and is stuck in a crash loop; " +
                $"reservations routed to it fail with 503 and those orders are rejected. {string.Join(" and ", others)} stay healthy, so the failure " +
                "rate stays below OrderService's circuit-breaker threshold.",
            ExpectedEvidence =
            [
                new(Services.Inventory, EventIds.MemoryUsageHigh, "Memory near the 4 GB limit right before each crash", [host]),
                new(Services.Inventory, EventIds.ProcessCrashed, "OutOfMemoryException in StockCache.RebuildAsync", [host]),
                new(Services.Inventory, EventIds.HealthCheckFailed, "Health checks returning 503", [host]),
                new(Services.Order, EventIds.InventoryCallFailed, $"Reservation calls fail with 503 from {host}"),
                new(Services.Order, EventIds.OrderRejectedInventoryUnavailable, "Orders rejected because inventory is unavailable"),
            ],
            CausalChain =
            [
                new(Services.Inventory, EventIds.MemoryUsageHigh, "Memory reaches the limit during the stock cache rebuild"),
                new(Services.Inventory, EventIds.ProcessCrashed, "OutOfMemoryException crash loop on one instance"),
                new(Services.Order, EventIds.InventoryCallFailed, "Reservations routed to that instance fail with 503"),
                new(Services.Order, EventIds.OrderRejectedInventoryUnavailable, "Those orders are rejected"),
            ],
            ExpectedAbsences =
            [
                new(AbsenceKind.NoEventOnHosts, Services.Inventory, "The other instances never crash", EventIds.ProcessCrashed, others),
                new(AbsenceKind.NoEventOnHosts, Services.Order, "OrderService's circuit breaker never opens",
                    EventIds.InventoryCircuitOpened, Services.HostsFor(Services.Order)),
            ],
            RecoveryEvidence =
            [
                new(Services.Inventory, EventIds.InstanceRecovered, "The instance is healthy again", [host]),
            ],
            Remediation =
            [
                $"Take {host} out of the load balancer until it is stable, and raise its memory limit",
                "Make the stock cache rebuild incremental/paged instead of loading everything into memory",
                "Retry reservations on another instance before failing the order",
            ],
        };
    }

    private static IReadOnlyList<string> CrashingHosts(ActiveIncident incident) =>
        incident.Variant.Hosts.Count > 0 ? incident.Variant.Hosts : InventoryHosts;

    private static bool AllInstances(ActiveIncident incident) => incident.Variant.Hosts.Count == 0;

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        var restart = 1;
        foreach (var host in CrashingHosts(incident))
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
        foreach (var host in CrashingHosts(incident))
        {
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 3000)), Levels.Error, Services.Inventory, EventIds.HealthCheckFailed,
                $"Health check failed for {host}: /health returned 503 (stock cache rebuild in progress)", host: host);
        }

        // The breaker trips after the failures it counts, so it is logged after this round of checks.
        if (AllInstances(incident) && !incident.State.ContainsKey(CircuitOpenKey))
        {
            incident.State[CircuitOpenKey] = "true";
            g.Emit(at.AddMilliseconds(g.Rng.Between(3100, 4000)), Levels.Warning, Services.Order, EventIds.InventoryCircuitOpened,
                "Circuit breaker for InventoryService opened after 5 consecutive failures");
        }

        // Crash loop: one instance dies again on every other check.
        if (incident.BackgroundTicks % 2 == 1)
            Crash(g, g.Rng.Pick(CrashingHosts(incident)), at.AddMilliseconds(g.Rng.Between(3000, 8000)), incident.BackgroundTicks / 2 + 2);
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        // Instances are still up until the first one runs out of memory.
        var firstCrash = new DateTime(long.Parse(incident.State[FirstCrashKey]), DateTimeKind.Utc);
        var host = f.Rng.Pick(InventoryHosts);
        f.Order.PinHost(Services.Inventory, host);
        if (f.Cursor < firstCrash || !CrashingHosts(incident).Contains(host) || !f.Rng.Chance(0.9))
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
            var from = AllInstances(incident) ? "" : $" ({host} returned 503)";
            f.Wait(ms).Error(Services.Order, EventIds.InventoryCallFailed, $"Failed to reserve inventory for order {o.OrderId}{from}", ServiceUnavailable, ms);
        }

        f.Wait(2, 10).Warning(Services.Order, EventIds.OrderRejectedInventoryUnavailable, $"Order {o.OrderId} rejected: inventory unavailable");
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in CrashingHosts(incident))
        {
            g.Emit(at, Levels.Information, Services.Inventory, EventIds.InstanceRecovered,
                $"Instance {host} healthy; stock cache rebuilt ({g.Rng.Between(45000, 52000)} SKUs)", host: host);
            at = at.AddMilliseconds(g.Rng.Between(500, 2000));
        }

        if (incident.State.ContainsKey(CircuitOpenKey))
            g.Emit(at.AddSeconds(5), Levels.Information, Services.Order, EventIds.InventoryCircuitClosed, "Circuit breaker for InventoryService closed");
    }

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng) =>
        (service, metric) switch
        {
            (Services.Inventory, "healthy_instances") => InventoryHosts.Count - CrashingHosts(incident).Count + (rng.Chance(0.2) ? 1 : 0),
            (Services.Inventory, "memory_mb") when AllInstances(incident) => rng.Between(3700, 4096),
            (Services.Inventory, "cpu_percent") when AllInstances(incident) => rng.Between(85.0, 99.0),
            _ => null
        };
}
