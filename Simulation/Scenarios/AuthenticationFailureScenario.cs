using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// Partial, distributed failure. Evidence chain the investigator has to piece together:
/// <code>
/// signing key rotation (new kid)
///   ├─ auth-1: cache refreshed with new kid ──► tokens validate
///   └─ auth-2/3: cache still holds old kid, last refresh ~45+ min ago ──► IDX10503 ──► 401 ──► checkout failures
/// </code>
/// No log line states the conclusion ("stale cache"); it has to be inferred from the per-host cache state.
/// </summary>
internal sealed class AuthenticationFailureScenario : IncidentScenarioBase
{
    private const string KeyIdKey = "kid";
    private const string PreviousKeyIdKey = "previousKid";
    private const string LastRefreshKeyPrefix = "lastRefreshMinutes:";
    private const int CacheStateEveryTicks = 4; // with a 15 s background interval: once a minute

    public override IncidentScenario Scenario => IncidentScenario.AuthenticationFailure;
    public override string Title => "Checkout authentication failures after key rotation";
    public override IncidentDifficulty Difficulty => IncidentDifficulty.Distributed;
    public override string RootCauseService => Services.Auth;
    public override string RootCause =>
        $"After a signing key rotation, {StaleHosts[0]} and {StaleHosts[1]} kept a stale signing key cache (old kid, not refreshed) and reject tokens " +
        $"signed with the new key id (IDX10503), so most checkouts fail with 401 Unauthorized; {HealthyHost} refreshed its cache and keeps working.";
    public override IReadOnlyList<string> AffectedServices => [Services.Auth, Services.Order];
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        new(Services.Auth, EventIds.SigningKeyRotated, "Signing key rotation introduces a new kid right before failures start"),
        new(Services.Auth, EventIds.SigningKeyCacheState, "Only the failing hosts still cache the previous kid and have not refreshed for 45+ minutes"),
        new(Services.Auth, EventIds.TokenValidationFailed, "IDX10503 for the new kid, only on the stale AuthService hosts"),
        new(Services.Order, EventIds.CheckoutUnauthorized, "Checkout requests rejected with 401"),
    ];
    public override IReadOnlyList<EvidenceHint> CausalChain =>
    [
        new(Services.Auth, EventIds.SigningKeyRotated, "Signing key rotated (new kid)"),
        new(Services.Auth, EventIds.SigningKeyCacheState, "Two instances keep the old kid in their cache"),
        new(Services.Auth, EventIds.TokenValidationFailed, "IDX10503 on those instances"),
        new(Services.Order, EventIds.CheckoutUnauthorized, "Checkouts rejected with 401"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Force a signing key (JWKS) cache refresh on the stale AuthService instances",
        "Publish new keys before signing with them and keep the previous key valid during rotation",
        "Alert on authentication failure rate and key cache age per instance",
    ];

    private static IReadOnlyList<string> AuthHosts => Services.HostsFor(Services.Auth);

    // The first host was refreshed by the rotation job; the others hold a stale cache.
    private static string HealthyHost => AuthHosts[0];
    private static IReadOnlyList<string> StaleHosts => [.. AuthHosts.Skip(1)];

    // During the incident, a generic "keys refreshed" message on a random host would contradict the evidence.
    public override bool SuppressesNoise(int eventId) => eventId == EventIds.JwksRefreshed;

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        var kid = "key-" + g.Rng.Hex(8);
        incident.State[KeyIdKey] = kid;
        incident.State[PreviousKeyIdKey] = "key-" + g.Rng.Hex(8);
        foreach (var host in StaleHosts)
            incident.State[LastRefreshKeyPrefix + host] = g.Rng.Between(44, 52).ToString();

        g.Emit(at, Levels.Information, Services.Auth, EventIds.SigningKeyRotated,
            $"Signing key rotation completed: new key id {kid}, previous key id {incident.State[PreviousKeyIdKey]}", host: HealthyHost);
        g.Emit(at.AddMilliseconds(g.Rng.Between(200, 800)), Levels.Information, Services.Auth, EventIds.SigningKeyCacheRefreshed,
            $"Signing key cache refreshed with kid {kid}", host: HealthyHost);

        // The rotation triggers a cache status report on every instance, logged before any request can fail.
        EmitCacheState(g, incident, at, jitter: false);
    }

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in StaleHosts)
        {
            g.Emit(at.AddMilliseconds(g.Rng.Between(0, 2000)), Levels.Warning, Services.Auth, EventIds.AuthFailureRateHigh,
                $"Elevated token validation failure rate on this instance: {g.Rng.Between(94, 100)}% of validations failed in the last minute", host: host);
        }

        if (incident.BackgroundTicks % CacheStateEveryTicks == CacheStateEveryTicks - 1)
            EmitCacheState(g, incident, at, jitter: true);
    }

    /// <summary>Periodic per-instance cache status: the key host-specific evidence.</summary>
    private static void EmitCacheState(LogGenerator g, ActiveIncident incident, DateTime at, bool jitter)
    {
        var elapsedMinutes = (int)(at - incident.StartedAt).TotalMinutes;
        foreach (var host in AuthHosts)
        {
            var (cachedKid, lastRefresh) = host == HealthyHost
                ? (incident.State[KeyIdKey], elapsedMinutes)
                : (incident.State[PreviousKeyIdKey], int.Parse(incident.State[LastRefreshKeyPrefix + host]) + elapsedMinutes);

            g.Emit(jitter ? at.AddMilliseconds(g.Rng.Between(0, 1500)) : at, Levels.Information, Services.Auth, EventIds.SigningKeyCacheState,
                $"Signing key cache contains kid {cachedKid}; last refresh was {lastRefresh} minute{(lastRefresh == 1 ? "" : "s")} ago", host: host);
        }
    }

    public override void RunOrder(Flow f, ActiveIncident incident)
    {
        var o = f.Order;
        var host = f.Rng.Pick(AuthHosts);
        o.PinHost(Services.Auth, host);
        if (host == HealthyHost)
        {
            CommonFlows.Normal(f);
            return;
        }

        incident.MarkAffected(o);
        var kid = incident.State[KeyIdKey];
        var ms = f.Rng.Between(2, 12);
        f.Info(Services.Order, EventIds.CheckoutReceived, $"Checkout request received for customer {o.CustomerId} ({o.ItemCount} items)")
         .Wait(ms).Error(Services.Auth, EventIds.TokenValidationFailed, $"Token validation failed for customer {o.CustomerId}",
            $"Microsoft.IdentityModel.Tokens.SecurityTokenSignatureKeyNotFoundException: IDX10503: Signature validation failed. Token does not have a kid matching any cached signing key. kid: '{kid}'.\n" +
            "   at Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler.ValidateSignature(JsonWebToken jwt, TokenValidationParameters parameters)",
            ms)
         .Wait(2, 10).Warning(Services.Order, EventIds.CheckoutUnauthorized, $"Checkout rejected for customer {o.CustomerId}: 401 Unauthorized from AuthService");
    }

    public override void OnEnd(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in StaleHosts)
        {
            g.Emit(at, Levels.Information, Services.Auth, EventIds.SigningKeyCacheRefreshed,
                $"Signing key cache refreshed with kid {incident.State[KeyIdKey]}", host: host);
            at = at.AddMilliseconds(g.Rng.Between(200, 1000));
        }
    }
}
