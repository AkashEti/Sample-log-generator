using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

internal sealed class AuthenticationFailureScenario : IncidentScenarioBase
{
    private const string KeyIdKey = "kid";

    public override IncidentScenario Scenario => IncidentScenario.AuthenticationFailure;
    public override string Title => "Checkout authentication failures after key rotation";
    public override string RootCauseService => Services.Auth;
    public override string RootCause =>
        "After a signing key rotation, two of the three AuthService instances kept a stale signing key cache and reject tokens signed with the new key id (IDX10503), so most checkouts fail with 401 Unauthorized.";
    public override IReadOnlyList<string> AffectedServices => [Services.Auth, Services.Order];
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence =>
    [
        new(Services.Auth, EventIds.SigningKeyRotated, "Signing key rotation right before failures start"),
        new(Services.Auth, EventIds.TokenValidationFailed, "IDX10503 signature key not found, only on some AuthService hosts"),
        new(Services.Order, EventIds.CheckoutUnauthorized, "Checkout requests rejected with 401"),
    ];
    public override IReadOnlyList<string> Remediation =>
    [
        "Force a signing key (JWKS) cache refresh on all AuthService instances",
        "Publish new keys before signing with them and keep the old key valid during rotation",
        "Alert on authentication failure rate per instance",
    ];

    private static IReadOnlyList<string> AuthHosts => Services.HostsFor(Services.Auth);

    // The first host was refreshed by the rotation job; the others hold a stale cache.
    private static string HealthyHost => AuthHosts[0];

    public override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        var kid = "key-" + g.Rng.Hex(8);
        incident.State[KeyIdKey] = kid;
        g.Emit(at, Levels.Information, Services.Auth, EventIds.SigningKeyRotated,
            $"Signing key rotation completed: new key id {kid}, previous key key-{g.Rng.Hex(8)} retired", host: HealthyHost);
    }

    public override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at) =>
        g.Emit(at, Levels.Warning, Services.Auth, EventIds.AuthFailureRateHigh,
            $"Elevated authentication failure rate: {g.Rng.Between(58, 72)}% of token validations failed in the last minute");

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
        foreach (var host in AuthHosts.Skip(1))
        {
            g.Emit(at, Levels.Information, Services.Auth, EventIds.SigningKeyCacheRefreshed,
                $"Signing key cache refreshed (kid {incident.State[KeyIdKey]})", host: host);
            at = at.AddMilliseconds(g.Rng.Between(200, 1000));
        }
    }
}
