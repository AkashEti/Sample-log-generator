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

    /// <summary>Not an outage: logs contain user-supplied prompt-injection text (Stage 4 security testing).</summary>
    PromptInjectionAttempt
}

public enum IncidentStatus
{
    Active,
    Resolved,
    Interrupted
}
