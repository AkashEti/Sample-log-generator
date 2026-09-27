using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation;

/// <summary>
/// Plausible but unrelated events emitted around an incident (difficulty levels 4-5). Each one looks like a
/// possible cause, so the investigator has to rule it out using the evidence. They are recorded in the incident's
/// ground truth (<see cref="IncidentRecord.Distractors"/>) so an evaluation can penalise blaming them.
/// </summary>
internal sealed record Distractor(string Service, int EventId, string Description, Action<LogGenerator, DateTime> Emit)
{
    public EvidenceHint ToHint() => new(Service, EventId, Description);

    public static IReadOnlyList<Distractor> All { get; } =
    [
        new(Services.Notification, EventIds.NotificationDeployed, "Unrelated NotificationService deployment", (g, at) =>
        {
            foreach (var host in Services.HostsFor(Services.Notification))
            {
                g.Emit(at, Levels.Information, Services.Notification, EventIds.NotificationDeployed,
                    "NotificationService v3.8.1 started (build 5120, previous v3.8.0)", host: host);
                at = at.AddMilliseconds(g.Rng.Between(800, 2500));
            }
        }),
        new(Services.Inventory, EventIds.GcPause, "Long garbage-collection pause on one InventoryService instance", (g, at) =>
            g.Emit(at, Levels.Warning, Services.Inventory, EventIds.GcPause,
                $"Gen2 garbage collection paused the process for {g.Rng.Between(600, 1400)} ms")),
        new(Services.Order, EventIds.FeatureFlagChanged, "Feature flag rollout change in OrderService", (g, at) =>
            g.Emit(at, Levels.Information, Services.Order, EventIds.FeatureFlagChanged,
                "Feature flag 'express-checkout' rollout changed from 10% to 25% of customers")),
        new(Services.Payment, EventIds.GatewayCertificateExpiring, "Payment gateway TLS certificate expiry warning", (g, at) =>
            g.Emit(at, Levels.Warning, Services.Payment, EventIds.GatewayCertificateExpiring,
                $"TLS certificate for api.paygate-sim.example expires in {g.Rng.Between(7, 14)} days")),
        new(Services.Auth, EventIds.ClientRateLimited, "Rate limiting of a single API client", (g, at) =>
            g.Emit(at, Levels.Warning, Services.Auth, EventIds.ClientRateLimited,
                $"Rate limit reached for client 'mobile-app-ios': {g.Rng.Between(420, 520)} req/s (limit 400)")),
        new(Services.Payment, EventIds.SlowQuery, "Burst of slow queries on payments-db", (g, at) =>
        {
            for (var i = 0; i < 3; i++)
            {
                var ms = g.Rng.Between(1500, 3500);
                g.Emit(at, Levels.Warning, Services.Payment, EventIds.SlowQuery,
                    $"Slow query on payments-db: SELECT * FROM payment_transactions WHERE created_at > @since took {ms} ms", durationMs: ms);
                at = at.AddMilliseconds(g.Rng.Between(2000, 8000));
            }
        }),
    ];
}
