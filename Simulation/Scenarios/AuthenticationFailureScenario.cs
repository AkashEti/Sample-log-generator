using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// Partial, distributed failure. Evidence chain the investigator has to piece together:
/// <code>
/// signing key rotation (new kid)
///   ├─ refreshed hosts: cache holds the new kid ──► tokens validate
///   └─ stale hosts: cache still holds old kid, last refresh ~45+ min ago ──► IDX10503 ──► 401 ──► checkout failures
/// </code>
/// Variants differ in which hosts are stale (one or two of the three). No log line states the conclusion
/// ("stale cache"); it has to be inferred from the per-host cache state.
/// </summary>
internal sealed class AuthenticationFailureScenario : IncidentScenarioBase
{
    private const string KeyIdKey = "kid";
    private const string PreviousKeyIdKey = "previousKid";
    private const string LastRefreshKeyPrefix = "lastRefreshMinutes:";
    private const int CacheStateEveryTicks = 4; // with a 15 s background interval: once a minute

    private static IReadOnlyList<string> AuthHosts => Services.HostsFor(Services.Auth);

    public override IncidentScenario Scenario => IncidentScenario.AuthenticationFailure;
    public override string Title => "Checkout authentication failures after key rotation";
    public override string RootCauseService => Services.Auth;
    public override IReadOnlyList<string> AffectedServices => [Services.Auth, Services.Order];

    // The defaults describe the classic form: the first host refreshed, the other two are stale.
    private static readonly ScenarioVariant Classic = StaleCache([.. AuthHosts.Skip(1)]);
    public override string RootCause => Classic.RootCause!;
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence => Classic.ExpectedEvidence!;
    public override IReadOnlyList<CausalLink> CausalChain => Classic.CausalChain!;
    public override IReadOnlyList<AbsenceCondition> ExpectedAbsences => Classic.ExpectedAbsences!;
    public override IReadOnlyList<EvidenceHint> RecoveryEvidence => Classic.RecoveryEvidence!;
    public override IReadOnlyList<string> Remediation =>
    [
        "Force a signing key (JWKS) cache refresh on the stale AuthService instances",
        "Publish new keys before signing with them and keep the previous key valid during rotation",
        "Alert on authentication failure rate and key cache age per instance",
    ];

    /// <summary>Two stale hosts (each host once the healthy one), or a single stale host.</summary>
    public override IReadOnlyList<ScenarioVariant> Variants =>
    [
        Classic,
        .. AuthHosts.Skip(1).Select(healthy => StaleCache([.. AuthHosts.Where(h => h != healthy)])),
        .. AuthHosts.Select(stale => StaleCache([stale])),
    ];

    private static ScenarioVariant StaleCache(IReadOnlyList<string> stale)
    {
        var healthy = AuthHosts.Where(h => !stale.Contains(h)).ToList();
        string Join(IEnumerable<string> hosts) => string.Join(" and ", hosts);
        return new ScenarioVariant($"stale-cache:{string.Join(",", stale)}", IncidentShape.Correlated)
        {
            Hosts = stale,
            RootCause =
                $"After a signing key rotation, {Join(stale)} kept a stale signing key cache (old kid, not refreshed) and reject tokens signed with " +
                $"the new key id (IDX10503), so checkouts routed to {(stale.Count == 1 ? "it" : "them")} fail with 401 Unauthorized; " +
                $"{Join(healthy)} refreshed {(healthy.Count == 1 ? "its" : "their")} cache and keep{(healthy.Count == 1 ? "s" : "")} working.",
            ExpectedEvidence =
            [
                new(Services.Auth, EventIds.SigningKeyRotated, "Signing key rotation introduces a new kid right before failures start"),
                new(Services.Auth, EventIds.SigningKeyCacheState, "Only the failing hosts still cache the previous kid and have not refreshed for 45+ minutes"),
                new(Services.Auth, EventIds.TokenValidationFailed, "IDX10503 for the new kid, only on the stale AuthService hosts", stale),
                new(Services.Order, EventIds.CheckoutUnauthorized, "Checkout requests rejected with 401"),
            ],
            CausalChain =
            [
                new(Services.Auth, EventIds.SigningKeyRotated, "Signing key rotated (new kid)"),
                new(Services.Auth, EventIds.SigningKeyCacheState, $"{stale.Count} instance{(stale.Count == 1 ? "" : "s")} keep the old kid in the cache"),
                new(Services.Auth, EventIds.TokenValidationFailed, "IDX10503 on those instances"),
                new(Services.Order, EventIds.CheckoutUnauthorized, "Checkouts rejected with 401"),
            ],
            ExpectedAbsences =
            [
                new(AbsenceKind.NoEventOnHosts, Services.Auth, "Instances that refreshed their key cache never reject a token", EventIds.TokenValidationFailed, healthy),
            ],
            RecoveryEvidence =
            [
                new(Services.Auth, EventIds.SigningKeyCacheRefreshed, "Stale instances refresh their key cache", stale),
            ],
        };
    }

    private static IReadOnlyList<string> StaleHosts(ActiveIncident incident) => incident.Variant.Hosts;
    private static IReadOnlyList<string> HealthyHosts(ActiveIncident incident) => [.. AuthHosts.Where(h => !incident.Variant.Hosts.Contains(h))];

    // During the incident, a generic "keys refreshed" message on a random host would contradict the evidence.
    public override bool SuppressesNoise(int eventId) => eventId == EventIds.JwksRefreshed;

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        var kid = "key-" + g.Rng.Hex(8);
        incident.State[KeyIdKey] = kid;
        incident.State[PreviousKeyIdKey] = "key-" + g.Rng.Hex(8);
        foreach (var host in StaleHosts(incident))
            incident.State[LastRefreshKeyPrefix + host] = g.Rng.Between(44, 52).ToString();

        // The rotation job runs on one refreshed instance; each refreshed instance reloads its cache right away.
        var rotator = HealthyHosts(incident)[0];
        g.Emit(at, Levels.Information, Services.Auth, EventIds.SigningKeyRotated,
            $"Signing key rotation completed: new key id {kid}, previous key id {incident.State[PreviousKeyIdKey]}", host: rotator);
        foreach (var host in HealthyHosts(incident))
            g.Emit(at.AddMilliseconds(g.Rng.Between(200, 800)), Levels.Information, Services.Auth, EventIds.SigningKeyCacheRefreshed,
                $"Signing key cache refreshed with kid {kid}", host: host);

        // The rotation triggers a cache status report on every instance, logged before any request can fail.
        EmitCacheState(g, incident, at, jitter: false);
    }

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        foreach (var host in StaleHosts(incident))
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
            var (cachedKid, lastRefresh) = !StaleHosts(incident).Contains(host)
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
        if (!StaleHosts(incident).Contains(host))
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
        foreach (var host in StaleHosts(incident))
        {
            g.Emit(at, Levels.Information, Services.Auth, EventIds.SigningKeyCacheRefreshed,
                $"Signing key cache refreshed with kid {incident.State[KeyIdKey]}", host: host);
            at = at.AddMilliseconds(g.Rng.Between(200, 1000));
        }
    }
}
