using SampleLogGenerator.Models;

namespace SampleLogGenerator.Simulation.Scenarios;

internal static class ScenarioCatalog
{
    public static IReadOnlyDictionary<IncidentScenario, IncidentScenarioBase> All { get; } =
        new IncidentScenarioBase[]
        {
            new PaymentGatewayTimeoutScenario(),
            new DatabaseConnectionPoolExhaustedScenario(),
            new InventoryServiceUnavailableScenario(),
            new AuthenticationFailureScenario(),
            new NetworkTimeoutScenario(),
            new GatewaySlowdownCascadeScenario(),
            new InventoryLockRetryStormScenario(),
            new NotificationBackpressureScenario(),
            new AuthCacheFailoverCascadeScenario(),
            new PromptInjectionScenario(),
        }.ToDictionary(s => s.Scenario);
}
