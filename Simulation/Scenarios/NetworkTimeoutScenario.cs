using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// Partial network failure: packet loss in one availability zone. Only the OrderService instance in that zone loses
/// connectivity to PaymentService.
/// <code>
/// order-1 (eu-west-1a), order-3 (eu-west-1c) ──► PaymentService OK
/// order-2 (eu-west-1b) ──► connect timeouts ──► retries ──► orders fail; PaymentService never sees these requests
/// </code>
/// That is the classic form; each availability zone is a variant.
/// Key evidence includes absences: no PaymentService logs for the failed orders, and no timeouts outside the zone.
/// </summary>
internal sealed class NetworkTimeoutScenario : IncidentScenarioBase
{
    private const string Endpoint = "payment-service.internal:8443";
    private const string TimeoutException =
        "System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.\n" +
        " ---> System.Net.Sockets.SocketException (110): Connection timed out\n" +
        "   at OrderService.Clients.PaymentClient.ChargeAsync(ChargeRequest request, CancellationToken ct)";

    private static IReadOnlyList<string> OrderHosts => Services.HostsFor(Services.Order);
    private static IReadOnlyList<string> Zones => [.. OrderHosts.Select(Services.ZoneFor).Distinct()];

    public override IncidentScenario Scenario => IncidentScenario.NetworkTimeout;
    public override string Title => "Zonal packet loss between OrderService and PaymentService";
    public override string RootCauseService => Services.Order;
    public override IReadOnlyList<string> AffectedServices => [Services.Order, Services.Payment, Services.Inventory];

    // The defaults describe the classic form (eu-west-1b); every zone is a variant.
    private static readonly ScenarioVariant Classic = ZoneVariant(Services.Region + "b");
    public override string RootCause => Classic.RootCause!;
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence => Classic.ExpectedEvidence!;
    public override IReadOnlyList<CausalLink> CausalChain => Classic.CausalChain!;
    public override IReadOnlyList<AbsenceCondition> ExpectedAbsences => Classic.ExpectedAbsences!;
    public override IReadOnlyList<EvidenceHint> RecoveryEvidence => Classic.RecoveryEvidence!;
    public override IReadOnlyList<string> Remediation => Classic.Remediation!;

    public override IReadOnlyList<ScenarioVariant> Variants => [Classic, .. Zones.Where(z => z != Services.Region + "b").Select(ZoneVariant)];

    private static ScenarioVariant ZoneVariant(string zone)
    {
        IReadOnlyList<string> isolated = [.. OrderHosts.Where(h => Services.ZoneFor(h) == zone)];
        IReadOnlyList<string> healthy = [.. OrderHosts.Where(h => Services.ZoneFor(h) != zone)];
        return new ScenarioVariant($"zone:{zone}", IncidentShape.Direct)
        {
            Hosts = isolated,
            RootCause =
                $"Packet loss in availability zone {zone} on the path to PaymentService ({Endpoint}): the OrderService instance in that zone " +
                $"({string.Join(", ", isolated)}) times out connecting, so its requests never reach PaymentService, which logs nothing for them. " +
                $"Instances in other zones ({string.Join(", ", healthy)}) are unaffected.",
            ExpectedEvidence =
            [
                new(Services.Order, EventIds.PaymentUpstreamDegraded, $"Connect time / packet loss to PaymentService, reported only from {zone}", isolated),
                new(Services.Order, EventIds.PaymentCallTimeout, $"SocketException: Connection timed out calling PaymentService, only from {zone}", isolated),
            ],
            CausalChain =
            [
                new(Services.Order, EventIds.PaymentUpstreamDegraded, $"Packet loss from {zone} to PaymentService"),
                new(Services.Order, EventIds.PaymentCallTimeout, "Connect timeouts calling PaymentService"),
                new(Services.Order, EventIds.OrderFailedPaymentUnreachable, "Orders fail: PaymentService unreachable"),
            ],
            ExpectedAbsences =
            [
                new(AbsenceKind.NoLogsForAffectedRequests, Services.Payment, "The failed requests never reached PaymentService, so it has no logs for them"),
                new(AbsenceKind.NoEventOnHosts, Services.Order, $"No connect timeouts from OrderService outside {zone}", EventIds.PaymentCallTimeout, healthy),
            ],
            RecoveryEvidence =
            [
                new(Services.Order, EventIds.PaymentConnectivityRestored, "Connectivity restored", isolated),
            ],
            Remediation =
            [
                $"Shift traffic away from {zone} / drain the OrderService instances there",
                "Check the network path, security groups and routing from that zone to PaymentService; escalate to the cloud provider",
                "Add connect-timeout alerts per upstream dependency and per zone",
            ],
        };
    }

    private static IReadOnlyList<string> IsolatedHosts(ActiveIncident incident) => incident.Variant.Hosts;

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at) => ReportDegraded(g, incident, at);

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at) => ReportDegraded(g, incident, at);

    private static void ReportDegraded(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in IsolatedHosts(incident))
        {
            g.Emit(at, Levels.Warning, Services.Order, EventIds.PaymentUpstreamDegraded,
                $"Upstream {Endpoint} degraded: TCP connect time {g.Rng.Between(2500, 9000)} ms, packet loss {g.Rng.Between(35, 70)}%", host: host);
        }
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        var host = f.Rng.Pick(OrderHosts);
        o.PinHost(Services.Order, host);
        if (!IsolatedHosts(incident).Contains(host) || !f.Rng.Chance(0.95))
        {
            CommonFlows.Normal(f);
            return;
        }

        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);
        if (!CommonFlows.ReserveInventory(f)) return;
        incident.MarkAffected(o);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var attemptStart = f.Cursor;
            f.Wait(10000).Error(Services.Order, EventIds.PaymentCallTimeout, $"HTTP request to PaymentService timed out for order {o.OrderId}",
                TimeoutException, 10000, f.TimedOut("PaymentClient.Charge", attemptStart, attempt));
            if (attempt == 1)
                f.Wait(500, 1500).Warning(Services.Order, EventIds.PaymentCallRetry, $"Retrying PaymentService call for order {o.OrderId} (attempt 2/2)");
        }

        f.Wait(5, 20).Error(Services.Order, EventIds.OrderFailedPaymentUnreachable, $"Order {o.OrderId} failed: PaymentService unreachable");
        CommonFlows.ReleaseInventory(f);
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in IsolatedHosts(incident))
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
