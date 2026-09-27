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

/// <summary>How hard the incident is to diagnose from the logs.</summary>
public enum IncidentDifficulty
{
    /// <summary>A single failing component, visible directly in its own logs.</summary>
    Direct = 1,

    /// <summary>A cause in one component surfaces as failures in others.</summary>
    Correlated = 2,

    /// <summary>A partial failure spread across hosts/services; the pattern must be pieced together.</summary>
    Distributed = 3,

    /// <summary>Plausible but unrelated events (distractors) compete with the real cause.</summary>
    CompetingHypotheses = 4,

    /// <summary>Heavy distractors or misleading/untrusted log content.</summary>
    NoisyMisleading = 5
}

public enum IncidentStatus
{
    Active,
    Resolved,
    Interrupted
}
