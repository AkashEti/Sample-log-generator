using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

/// <summary>
/// A failure that spreads hop by hop. The incident moves through stages (stage 0 = the root cause) at fixed
/// fractions of its duration. Each stage announces itself with log lines when first reached, keeps emitting
/// its own symptoms, and changes how requests behave from then on. Requests only see a stage after it has
/// been announced, so in the logs the cause always appears before its effects.
/// Every cascade has a full variant and a contained one that stops at <see cref="ContainedAt"/>: the same root cause,
/// caught before it spreads, so it has fewer hops and a different root-cause statement.
/// </summary>
internal abstract class CascadeScenarioBase : IncidentScenarioBase
{
    private const string StageKey = "stage";

    public override TimeSpan BackgroundInterval => TimeSpan.FromSeconds(5);

    /// <summary>The last stage of the contained variant.</summary>
    protected abstract int ContainedAt { get; }

    /// <summary>Shape of the contained variant.</summary>
    protected virtual IncidentShape ContainedShape => IncidentShape.Correlated;

    /// <summary>Root cause statement of the contained variant.</summary>
    protected abstract string ContainedRootCause { get; }

    public override IReadOnlyList<ScenarioVariant> Variants =>
    [
        new("full-cascade", IncidentShape.Cascade),
        new($"contained-at-stage-{ContainedAt}", ContainedShape) { MaxStage = ContainedAt, RootCause = ContainedRootCause },
    ];

    // For a cascade, the evidence to find is the propagation path itself.
    public override IReadOnlyList<EvidenceHint> ExpectedEvidence => [.. CausalChain.Select(l => l.ToHint())];

    /// <summary>Fraction of the incident at which each stage begins. Index 0 is the root cause and must be 0.</summary>
    protected abstract IReadOnlyList<double> StageStarts { get; }

    /// <summary>The highest stage announced so far.</summary>
    protected static int Stage(ActiveIncident incident) => int.Parse(incident.State[StageKey]);

    public sealed override void OnStart(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        incident.State[StageKey] = "0";
        OnStageReached(g, incident, 0, at);
    }

    public sealed override void OnBackground(LogGenerator g, ActiveIncident incident, DateTime at)
    {
        var reached = Math.Min(StageAt(incident.Progress(at)), incident.Variant.MaxStage ?? int.MaxValue);
        for (var stage = Stage(incident) + 1; stage <= reached; stage++)
        {
            incident.State[StageKey] = stage.ToString();
            OnStageReached(g, incident, stage, at);
            at = at.AddMilliseconds(g.Rng.Between(200, 800));
        }

        OnSymptoms(g, incident, Stage(incident), at);
    }

    /// <summary>One-off log lines marking the moment the failure reaches the next hop.</summary>
    protected abstract void OnStageReached(LogGenerator g, ActiveIncident incident, int stage, DateTime at);

    /// <summary>Recurring log lines for every stage reached so far.</summary>
    protected abstract void OnSymptoms(LogGenerator g, ActiveIncident incident, int stage, DateTime at);

    private int StageAt(double progress)
    {
        var stage = 0;
        for (var i = 1; i < StageStarts.Count; i++)
            if (progress >= StageStarts[i]) stage = i;
        return stage;
    }
}

/// <summary>Exception text shared by several scenarios, formatted like real .NET stack traces.</summary>
internal static class Exceptions
{
    public const string PoolTimeout =
        "System.InvalidOperationException: Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool. " +
        "This may have occurred because all pooled connections were in use and max pool size was reached.\n" +
        "   at Npgsql.ConnectorPool.Get(NpgsqlConnection conn, NpgsqlTimeout timeout, Boolean async, CancellationToken ct)\n" +
        "   at PaymentService.Data.PaymentRepository.SaveTransactionAsync(PaymentTransaction tx, CancellationToken ct)";

    public const string GatewayTimeout =
        "System.TimeoutException: The operation has timed out after 30000 ms.\n" +
        "   at PaymentService.Gateway.PayGateClient.AuthorizeAsync(PaymentRequest request, CancellationToken ct)\n" +
        "   at PaymentService.Payments.PaymentProcessor.AuthorizeAsync(Order order, CancellationToken ct)";

    /// <summary>
    /// HttpClient.Timeout elapsed while waiting for a response. There is no SocketException: the connection was made
    /// and the request reached the other service, which is still working on it.
    /// </summary>
    public static string HttpClientTimeout(string client, string method, int seconds) =>
        $"System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of {seconds} seconds elapsing.\n" +
        " ---> System.TimeoutException: The operation was canceled.\n" +
        $"   at OrderService.Clients.{client}.{method}(CancellationToken ct)";
}
