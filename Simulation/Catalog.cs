namespace SampleLogGenerator.Simulation;

public static class Services
{
    public const string Order = "OrderService";
    public const string Auth = "AuthService";
    public const string Inventory = "InventoryService";
    public const string Payment = "PaymentService";
    public const string Notification = "NotificationService";

    public static readonly IReadOnlyList<string> All = [Order, Auth, Inventory, Payment, Notification];

    private static readonly Dictionary<string, string[]> Hosts = new()
    {
        [Order] = ["order-svc-7c9f4-1", "order-svc-7c9f4-2", "order-svc-7c9f4-3"],
        [Auth] = ["auth-svc-5d6b8-1", "auth-svc-5d6b8-2", "auth-svc-5d6b8-3"],
        [Inventory] = ["inventory-svc-84c2a-1", "inventory-svc-84c2a-2", "inventory-svc-84c2a-3"],
        [Payment] = ["payment-svc-6a1e7-1", "payment-svc-6a1e7-2", "payment-svc-6a1e7-3"],
        [Notification] = ["notification-svc-3f0b9-1", "notification-svc-3f0b9-2"],
    };

    public static IReadOnlyList<string> HostsFor(string service) => Hosts[service];
}

public static class Levels
{
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Critical = "Critical";

    public static int Rank(string? level) => level?.ToLowerInvariant() switch
    {
        "information" or "info" => 1,
        "warning" or "warn" => 2,
        "error" => 3,
        "critical" or "fatal" => 4,
        _ => 0
    };
}

/// <summary>
/// Stable event ids per message template (like EventId in Microsoft.Extensions.Logging),
/// so the same kind of event can always be searched for by id.
/// </summary>
public static class EventIds
{
    // OrderService 1xxx
    public const int CheckoutReceived = 1001;
    public const int OrderCreated = 1002;
    public const int OrderConfirmed = 1003;
    public const int PaymentPending = 1004;
    public const int OrderCancelledPaymentDeclined = 1005;
    public const int OrderPaymentFailed = 1006;
    public const int OrderCancelledOutOfStock = 1007;
    public const int AbandonedCartCleanup = 1010;
    public const int InventoryCircuitOpened = 1101;
    public const int InventoryCallFailed = 1102;
    public const int OrderRejectedInventoryUnavailable = 1103;
    public const int InventoryCircuitClosed = 1104;
    public const int CheckoutUnauthorized = 1105;
    public const int PaymentUpstreamDegraded = 1201;
    public const int PaymentCallTimeout = 1202;
    public const int PaymentCallRetry = 1203;
    public const int OrderFailedPaymentUnreachable = 1204;
    public const int PaymentConnectivityRestored = 1205;

    // AuthService 2xxx
    public const int TokenValidated = 2001;
    public const int JwksRefreshed = 2010;
    public const int SigningKeyRotated = 2101;
    public const int TokenValidationFailed = 2102;
    public const int AuthFailureRateHigh = 2103;
    public const int SigningKeyCacheRefreshed = 2104;

    // InventoryService 3xxx
    public const int StockReserved = 3001;
    public const int InsufficientStock = 3003;
    public const int ReservationReleased = 3004;
    public const int StockCacheRefreshed = 3010;
    public const int ProcessCrashed = 3101;
    public const int InstanceRestarting = 3102;
    public const int HealthCheckFailed = 3103;
    public const int InstanceRecovered = 3104;

    // PaymentService 4xxx
    public const int ServiceDeployed = 4000;
    public const int PaymentProcessing = 4001;
    public const int PaymentAuthorized = 4002;
    public const int PaymentDeclined = 4003;
    public const int GatewaySlowResponse = 4005;
    public const int SlowQuery = 4010;
    public const int GatewayLatencyHigh = 4101;
    public const int GatewayResponseDelayed = 4102;
    public const int GatewayTimeout = 4103;
    public const int PaymentRetry = 4104;
    public const int PaymentFailed = 4105;
    public const int GatewayLatencyRecovered = 4106;
    public const int ConnectionPoolUsageHigh = 4201;
    public const int ConnectionPoolExhausted = 4202;
    public const int PaymentPersistFailed = 4203;
    public const int ConnectionPoolRecovered = 4204;

    // NotificationService 5xxx
    public const int EmailQueued = 5001;
    public const int EmailDelivered = 5002;
    public const int EmailBounced = 5003;
    public const int SmtpConnectionRecycled = 5010;
    public const int TemplateRenderWarning = 5101;
}
