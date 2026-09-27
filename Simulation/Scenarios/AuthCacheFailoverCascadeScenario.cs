using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// <code>
/// [0] Redis failover (AuthService's token-revocation cache) → new primary starts cold → lookups fall back to auth-db
///  └► [1] token validation slows down (still under OrderService's 3 s timeout)
///      └► [2] validation exceeds the timeout → OrderService retries → AuthService load doubles
///           └► [3] AuthService hits its concurrency limit and sheds load with 429 → checkouts rejected
/// </code>
/// It looks like the rate limiting of one client (a distractor) or an AuthService capacity problem;
/// the origin is the cache failover.
/// </summary>
internal sealed class AuthCacheFailoverCascadeScenario : CascadeScenarioBase
{
    private const string Cache = "auth-cache:6379";
    private const int OrderTimeoutMs = 3000;
    private const int MaxAttempts = 2;

    public override IncidentScenario Scenario => IncidentScenario.AuthCacheFailoverCascade;
    public override string Title => "Auth cache failover escalating into checkout load shedding";
    public override string RootCauseService => Services.Auth;
    public override string RootCause =>
        $"A Redis failover of AuthService's token-revocation cache ({Cache}) left it with a cold cache, so revocation checks fell back to auth-db. " +
        $"Token validation slowed past OrderService's {OrderTimeoutMs / 1000} s timeout; OrderService's retries doubled the load until AuthService hit its " +
        "concurrency limit and shed requests with 429, rejecting checkouts.";
    public override IReadOnlyList<string> AffectedServices => [Services.Auth, Services.Order];
    protected override IReadOnlyList<double> StageStarts => [0, 0.15, 0.35, 0.6];
    public override IReadOnlyList<EvidenceHint> CausalChain =>
    [
        new(Services.Auth, EventIds.CacheConnectionLost, $"Connection to {Cache} lost; failover to a new primary"),
        new(Services.Auth, EventIds.RevocationCacheMisses, "Revocation cache hit ratio collapses; lookups served from auth-db"),
        new(Services.Auth, EventIds.TokenValidationSlow, "Token validation latency climbs"),
        new(Services.Order, EventIds.AuthCallTimeout, "OrderService's calls to AuthService time out after 3 s"),
        new(Services.Order, EventIds.AuthCallRetry, "OrderService retries every timed-out validation"),
        new(Services.Auth, EventIds.LoadShedding, "AuthService hits its concurrency limit and sheds load (429)"),
        new(Services.Order, EventIds.CheckoutThrottled, "Checkouts rejected with 429"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Warm the revocation cache after a Redis failover (or fail over to a replica that already has the data)",
        "Keep a short-lived in-process cache of revocation lookups so a Redis outage does not send every lookup to auth-db",
        "Add a retry budget and backoff to OrderService's auth client, since retries amplify an overloaded dependency",
        "Scale AuthService or raise its concurrency limit temporarily",
    ];

    private static int HitRatio(ActiveIncident incident, DateTime at, Random rng) =>
        (int)(3 + 37 * incident.Progress(at)) + rng.Between(0, 3);

    private static int ValidationMs(int stage, Random rng) => stage switch
    {
        0 => rng.Between(150, 600),
        1 => rng.Between(800, 2800),
        _ => rng.Between(1500, 6000),
    };

    protected override void OnStageReached(LogGenerator g, ActiveIncident incident, int stage, DateTime at)
    {
        switch (stage)
        {
            case 1:
                g.Emit(at, Levels.Warning, Services.Auth, EventIds.TokenValidationSlow,
                    $"Token validation slow: p95 {ValidationMs(1, g.Rng)} ms over the last minute (auth-db query p95 {g.Rng.Between(400, 1800)} ms)");
                return;
            case 3:
                g.Emit(at, Levels.Warning, Services.Auth, EventIds.LoadShedding,
                    $"Concurrency limit reached: {g.Rng.Between(256, 300)} in-flight validations (limit 256); shedding load with 429");
                return;
            case not 0:
                return;
        }

        foreach (var host in Services.HostsFor(Services.Auth))
        {
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 500)), Levels.Warning, Services.Auth, EventIds.CacheConnectionLost,
                $"Redis connection to {Cache} lost (SocketClosed); reconnecting", host: host);
            g.Emit(at.AddMilliseconds(g.Rng.Between(3000, 6000)), Levels.Information, Services.Auth, EventIds.CacheFailoverCompleted,
                $"Reconnected to {Cache} after failover; new primary auth-cache-1", host: host);
        }
    }

    protected override void OnSymptoms(LogGenerator g, ActiveIncident incident, int stage, DateTime at)
    {
        foreach (var host in Services.HostsFor(Services.Auth))
        {
            var hostAt = at.AddMilliseconds(g.Rng.Between(0, 2000));
            g.Emit(hostAt, Levels.Warning, Services.Auth, EventIds.RevocationCacheMisses,
                $"Token revocation cache hit ratio {HitRatio(incident, hostAt, g.Rng)}% over the last minute; {g.Rng.Between(1200, 4000)} lookups served from auth-db", host: host);

            if (stage >= 1)
                g.Emit(hostAt.AddMilliseconds(g.Rng.Between(100, 800)), Levels.Warning, Services.Auth, EventIds.TokenValidationSlow,
                    $"Token validation slow: p95 {ValidationMs(stage, g.Rng)} ms over the last minute (auth-db query p95 {g.Rng.Between(400, 1800)} ms)", host: host);

            if (stage >= 3)
                g.Emit(hostAt.AddMilliseconds(g.Rng.Between(100, 800)), Levels.Warning, Services.Auth, EventIds.LoadShedding,
                    $"Concurrency limit reached: {g.Rng.Between(256, 300)} in-flight validations (limit 256); shedding load with 429", host: host);
        }
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        var stage = Stage(incident);
        f.Info(Services.Order, EventIds.CheckoutReceived, $"Checkout request received for customer {o.CustomerId} ({o.ItemCount} items)");

        if (stage >= 3 && f.Rng.Chance(0.8))
        {
            incident.MarkAffected(o);
            f.Wait(2, 15).Warning(Services.Order, EventIds.CheckoutThrottled, $"Checkout rejected for customer {o.CustomerId}: 429 Too Many Requests from AuthService");
            return;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var attemptStart = f.Cursor;
            var ms = ValidationMs(stage, f.Rng);
            if (ms < OrderTimeoutMs)
            {
                f.Wait(ms).Info(Services.Auth, EventIds.TokenValidated, $"Access token validated for customer {o.CustomerId}", ms);
                CommonFlows.CreateOrder(f);
                if (!CommonFlows.ReserveInventory(f)) return;
                if (CommonFlows.ProcessPayment(f)) CommonFlows.ConfirmAndNotify(f);
                return;
            }

            // AuthService still finishes the validation after OrderService stopped waiting.
            incident.MarkAffected(o);
            f.Wait(ms).Info(Services.Auth, EventIds.TokenValidated, $"Access token validated for customer {o.CustomerId}", ms);
            f.At(attemptStart).Wait(OrderTimeoutMs)
             .Error(Services.Order, EventIds.AuthCallTimeout, $"Timed out waiting for AuthService to validate token for customer {o.CustomerId} (attempt {attempt}/{MaxAttempts})",
                Exceptions.HttpClientTimeout("AuthClient", "ValidateTokenAsync", OrderTimeoutMs / 1000), OrderTimeoutMs);
            if (attempt < MaxAttempts)
                f.Wait(0, 50).Warning(Services.Order, EventIds.AuthCallRetry, $"Retrying AuthService token validation for customer {o.CustomerId} (attempt {attempt + 1}/{MaxAttempts})");
        }

        f.Wait(2, 10).Error(Services.Order, EventIds.CheckoutFailedAuthUnavailable, $"Checkout failed for customer {o.CustomerId}: AuthService unavailable");
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in Services.HostsFor(Services.Auth))
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 3000)), Levels.Information, Services.Auth, EventIds.RevocationCacheWarm,
                $"Token revocation cache warm: hit ratio {g.Rng.Between(95, 99)}% over the last minute", host: host);
    }

    public override double? Gauge(string service, string metric, ActiveIncident incident, DateTime at, Random rng)
    {
        var stage = Stage(incident);
        return (service, metric) switch
        {
            (Services.Auth, "cache_hit_ratio_percent") => HitRatio(incident, at, rng),
            (Services.Auth, "cpu_percent") => stage >= 2 ? rng.Between(85.0, 99.0) : null,
            _ => null
        };
    }
}
