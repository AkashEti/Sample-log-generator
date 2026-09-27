namespace SampleLogGenerator.Models;

/// <summary>
/// Known failure scenarios the generator can inject. Normal traffic is always running in the background.
/// </summary>
public enum IncidentScenario
{
    PaymentGatewayTimeout,
    DatabaseConnectionPoolExhausted,
    InventoryServiceUnavailable,
    AuthenticationFailure,
    NetworkTimeout,

    // Cascading failures: one fault propagates hop by hop through the system over the incident's lifetime.
    GatewaySlowdownCascade,
    InventoryLockRetryStorm,
    NotificationBackpressure,
    AuthCacheFailoverCascade,

    /// <summary>Not an outage: logs contain user-supplied prompt-injection text (Stage 4 security testing).</summary>
    PromptInjectionAttempt
}

/// <summary>
/// How hard the incident is to diagnose. Computed per incident from what it contains (the hardest feature wins), so the
/// same scenario appears at several levels.
/// </summary>
public enum IncidentDifficulty
{
    /// <summary>Root cause leads straight to the impact; no decoys.</summary>
    Clean = 1,

    /// <summary>Benign noise or alarming-but-irrelevant errors (misleading evidence) around the incident.</summary>
    Distractors = 2,

    /// <summary>The failure crosses three or more causal hops before users see it.</summary>
    Cascade = 3,

    /// <summary>A plausible but non-causal explanation (competing change, correlated event) must be ruled out.</summary>
    CompetingHypotheses = 4,

    /// <summary>Malicious or instruction-like content in the logs on top of the incident.</summary>
    Adversarial = 5
}

/// <summary>How a failure travels from its root cause to user impact. A scenario has variants of different shapes.</summary>
public enum IncidentShape
{
    /// <summary>The failing component's own logs show both the cause and the impact.</summary>
    Direct,

    /// <summary>The cause in one component (or a subset of hosts) surfaces as failures somewhere else.</summary>
    Correlated,

    /// <summary>Three or more causal hops, typically across services.</summary>
    Cascade
}

/// <summary>
/// Evaluation bucket an incident was planned for (see <c>--mix</c>). It decides the variant shape and the decoys.
/// </summary>
public enum EvaluationProfile
{
    /// <summary>Direct variant, no decoys.</summary>
    Clean,

    /// <summary>Correlated variant, no decoys.</summary>
    Correlated,

    /// <summary>Cascade variant, no decoys.</summary>
    Cascade,

    /// <summary>Any variant plus benign noise and misleading evidence.</summary>
    Noisy,

    /// <summary>Any variant plus competing and correlated-but-not-causal decoys.</summary>
    CompetingHypotheses,

    /// <summary>Any variant plus prompt-injection / hostile content and a competing decoy.</summary>
    Adversarial
}

public enum IncidentStatus
{
    Active,
    Resolved,
    Interrupted
}
