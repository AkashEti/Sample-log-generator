using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// Back-pressure travels upstream: the root cause is at the end of the checkout flow, but checkout breaks at the front.
/// <code>
/// [0] SMTP relay times out (NotificationService) → email workers block on 30 s sends
///  └► [1] all workers busy → order-events queue stops draining; backlog grows
///      └► [2] backlog trips the broker's memory alarm → broker blocks publishers (flow control)
///           └► [3] OrderService cannot publish OrderConfirmed → paid orders stuck in PaymentCaptured
/// </code>
/// The errors are loudest in OrderService, which only publishes to the queue.
/// </summary>
internal sealed class NotificationBackpressureScenario : CascadeScenarioBase
{
    private const string SmtpRelay = "smtp-relay.mail-sim.example:587";
    private const string Queue = "order-events";
    private const int Workers = 16;
    private const string SmtpTimeout =
        "System.Net.Mail.SmtpException: The operation has timed out.\n" +
        "   at System.Net.Mail.SmtpClient.SendAsync(MailMessage message, CancellationToken ct)\n" +
        "   at NotificationService.Email.SmtpEmailSender.SendAsync(EmailMessage email, CancellationToken ct)";
    private const string PublishTimeout =
        "System.TimeoutException: Publisher confirm not received within 00:00:05 for exchange 'order-events' (connection blocked by broker: low on memory).\n" +
        "   at OrderService.Messaging.EventPublisher.PublishAsync(IntegrationEvent evt, CancellationToken ct)";

    public override IncidentScenario Scenario => IncidentScenario.NotificationBackpressure;
    public override string Title => "SMTP slowdown back-pressuring order confirmation";
    public override string RootCauseService => Services.Notification;
    public override string RootCause =>
        $"The SMTP relay ({SmtpRelay}) used by NotificationService started timing out. Email workers blocked on 30 s sends, so NotificationService " +
        $"stopped draining the {Queue} queue; the backlog tripped the message broker's memory alarm, which blocks publishers, so OrderService could not " +
        "publish OrderConfirmed and paid orders were left in PaymentCaptured.";
    public override IReadOnlyList<string> AffectedServices => [Services.Notification, Services.Order];
    protected override IReadOnlyList<double> StageStarts => [0, 0.2, 0.4, 0.55];
    protected override int ContainedAt => 1;
    protected override IncidentShape ContainedShape => IncidentShape.Direct;
    protected override string ContainedRootCause =>
        $"The SMTP relay ({SmtpRelay}) used by NotificationService started timing out. Email workers blocked on 30 s sends, so confirmation " +
        "emails failed or waited in the queue. The relay recovered before the backlog was large enough to throttle the message broker, " +
        "so orders themselves were not affected.";
    public override IReadOnlyList<CausalLink> CausalChain =>
    [
        new(Services.Notification, EventIds.SmtpTimeout, "SMTP sends to the relay time out after 30 s"),
        new(Services.Notification, EventIds.WorkersSaturated, "Every email worker is blocked; prefetch limit reached"),
        new(Services.Notification, EventIds.ConsumerLag, $"Backlog on the {Queue} queue keeps growing"),
        new(Services.Order, EventIds.BrokerFlowControl, "Broker blocks publishers (memory alarm)"),
        new(Services.Order, EventIds.EventPublishTimeout, "OrderService times out publishing OrderConfirmed"),
        new(Services.Order, EventIds.OrderAwaitingConfirmation, "Paid orders stuck in PaymentCaptured"),
    ];
    public override IReadOnlyList<EvidenceHint> RecoveryEvidence =>
    [
        new(Services.Notification, EventIds.SmtpRecovered, "SMTP relay responding again; backlog draining"),
        new(Services.Order, EventIds.BrokerFlowControlLifted, "Broker flow control lifted"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Fail over to a secondary SMTP relay/provider and lower the SMTP send timeout",
        $"Cap the {Queue} queue (max-length, dead-letter or lazy queue) so one slow consumer cannot trigger a broker-wide memory alarm",
        "Publish order events through an outbox on a separate connection so broker flow control cannot block checkout",
        "Re-drive the orders stuck in PaymentCaptured once publishing recovers",
    ];

    private static int QueueDepth(int stage, ActiveIncident incident, DateTime at, Random rng) =>
        stage == 0 ? rng.Between(200, 900) : 1000 + (int)(60000 * incident.Progress(at)) + rng.Between(0, 500);

    protected override void OnStageReached(LogGenerator g, ActiveIncident incident, int stage, DateTime at)
    {
        switch (stage)
        {
            case 0:
                g.Emit(at, Levels.Warning, Services.Notification, EventIds.SmtpTimeout,
                    $"SMTP send to {SmtpRelay} timed out after 30000 ms", exception: SmtpTimeout, durationMs: 30000);
                break;
            case 1:
                g.Emit(at, Levels.Warning, Services.Notification, EventIds.WorkersSaturated,
                    $"All {Workers} email workers busy; consumer prefetch limit reached on queue {Queue}");
                break;
            case 2:
                foreach (var host in Services.HostsFor(Services.Order))
                    g.Emit(host == Services.HostsFor(Services.Order)[0] ? at : at.AddMilliseconds(g.Rng.Between(0, 1500)), Levels.Warning, Services.Order, EventIds.BrokerFlowControl,
                        $"Message broker flow control active: publishing to exchange {Queue} blocked (broker memory alarm)", host: host);
                break;
        }
    }

    protected override void OnSymptoms(LogGenerator g, ActiveIncident incident, int stage, DateTime at)
    {
        foreach (var host in Services.HostsFor(Services.Notification))
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 2000)), Levels.Warning, Services.Notification, EventIds.SmtpTimeout,
                $"SMTP send to {SmtpRelay} timed out after 30000 ms", exception: SmtpTimeout, durationMs: 30000, host: host);

        if (stage >= 1)
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 2000)), Levels.Warning, Services.Notification, EventIds.ConsumerLag,
                $"Consumer lag on queue {Queue}: {QueueDepth(stage, incident, at, g.Rng)} messages, oldest {(int)(at - incident.StartedAt).TotalSeconds} s");

        if (stage >= 2)
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 2000)), Levels.Warning, Services.Order, EventIds.BrokerFlowControl,
                $"Message broker flow control active: publishing to exchange {Queue} blocked (broker memory alarm)");
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        var stage = Stage(incident);
        CommonFlows.Checkout(f);
        CommonFlows.CreateOrder(f);
        if (!CommonFlows.ReserveInventory(f)) return;
        if (!CommonFlows.ProcessPayment(f)) return;

        if (stage >= 3 && f.Rng.Chance(0.85))
        {
            incident.MarkAffected(o);
            var publishStart = f.Cursor;
            f.Wait(5000).Error(Services.Order, EventIds.EventPublishTimeout, $"Timed out publishing OrderConfirmed for order {o.OrderId} after 5000 ms", PublishTimeout, 5000,
                f.TimedOut("EventPublisher.Publish", publishStart))
             .Wait(5, 20).Warning(Services.Order, EventIds.OrderAwaitingConfirmation,
                $"Order {o.OrderId} left in state PaymentCaptured; OrderConfirmed queued in outbox for retry");
            return;
        }

        // Publishing is slow while the broker is throttling.
        var publishMs = stage >= 2 ? f.Rng.Between(1200, 4800) : f.Rng.Between(5, 20);
        f.Wait(publishMs).Info(Services.Order, EventIds.OrderConfirmed, $"Order {o.OrderId} confirmed", stage >= 2 ? publishMs : null);

        if (stage >= 1) return; // the confirmation email sits in the backlog; NotificationService never gets to it

        f.Wait(10, 60).Info(Services.Notification, EventIds.EmailQueued, $"Order confirmation email queued for order {o.OrderId}");
        if (f.Rng.Chance(0.5))
        {
            incident.MarkAffected(o); // the customer gets no confirmation email
            f.Wait(30000).Warning(Services.Notification, EventIds.EmailSendFailed,
                $"Confirmation email for order {o.OrderId} not sent: SMTP timeout after 30000 ms; will retry", SmtpTimeout, 30000);
        }
        else
        {
            var ms = f.Rng.Between(4000, 25000);
            f.Wait(ms).Info(Services.Notification, EventIds.EmailDelivered, $"Confirmation email delivered for order {o.OrderId}", ms);
        }
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        g.Emit(at, Levels.Information, Services.Notification, EventIds.SmtpRecovered,
            $"SMTP relay {SmtpRelay} responding normally; draining {QueueDepth(Stage(incident), incident, at, g.Rng)} queued messages");
        if (Stage(incident) >= 2)
            g.Emit(at.AddSeconds(g.Rng.Between(20, 60)), Levels.Information, Services.Order, EventIds.BrokerFlowControlLifted,
                $"Message broker flow control lifted for exchange {Queue}");
    }

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng) =>
        (service, metric) switch
        {
            (Services.Notification, "queue_depth") => QueueDepth(Stage(incident), incident, at, rng),
            _ => null
        };
}
