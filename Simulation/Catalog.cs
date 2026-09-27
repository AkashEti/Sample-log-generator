namespace SampleLogGenerator.Simulation;

public static class Services
{
    public const string Order = "OrderService";
    public const string Auth = "AuthService";
    public const string Inventory = "InventoryService";
    public const string Payment = "PaymentService";
    public const string Notification = "NotificationService";
    public const string Catalog = "CatalogService";
    public const string Cart = "CartService";

    public static readonly IReadOnlyList<string> All = [Catalog, Cart, Order, Auth, Inventory, Payment, Notification];

    private static readonly Dictionary<string, string[]> Hosts = new()
    {
        [Order] = ["order-svc-7c9f4-1", "order-svc-7c9f4-2", "order-svc-7c9f4-3"],
        [Auth] = ["auth-svc-5d6b8-1", "auth-svc-5d6b8-2", "auth-svc-5d6b8-3"],
        [Inventory] = ["inventory-svc-84c2a-1", "inventory-svc-84c2a-2", "inventory-svc-84c2a-3"],
        [Payment] = ["payment-svc-6a1e7-1", "payment-svc-6a1e7-2", "payment-svc-6a1e7-3"],
        [Notification] = ["notification-svc-3f0b9-1", "notification-svc-3f0b9-2"],
        [Catalog] = ["catalog-svc-9b2d1-1", "catalog-svc-9b2d1-2", "catalog-svc-9b2d1-3"],
        [Cart] = ["cart-svc-2e7c5-1", "cart-svc-2e7c5-2"],
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
    public const int FeatureFlagChanged = 1011;
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
    public const int InventoryCallTimeout = 1106;
    public const int InventoryCallRetry = 1107;
    public const int BrokerFlowControl = 1301;
    public const int EventPublishTimeout = 1302;
    public const int OrderAwaitingConfirmation = 1303;
    public const int AuthCallTimeout = 1304;
    public const int AuthCallRetry = 1305;
    public const int CheckoutThrottled = 1306;
    public const int CheckoutFailedAuthUnavailable = 1307;
    public const int LatePaymentForFailedOrder = 1308;
    public const int BrokerFlowControlLifted = 1310;

    // AuthService 2xxx
    public const int TokenValidated = 2001;
    public const int JwksRefreshed = 2010;
    public const int ClientRateLimited = 2011;
    public const int UserSignedIn = 2020;
    public const int SignInFailed = 2021;
    public const int UserSignedOut = 2022;
    public const int SigningKeyRotated = 2101;
    public const int TokenValidationFailed = 2102;
    public const int AuthFailureRateHigh = 2103;
    public const int SigningKeyCacheRefreshed = 2104;
    public const int SigningKeyCacheState = 2105;
    public const int CacheConnectionLost = 2201;
    public const int CacheFailoverCompleted = 2202;
    public const int RevocationCacheMisses = 2203;
    public const int TokenValidationSlow = 2204;
    public const int LoadShedding = 2205;
    public const int RevocationCacheWarm = 2207;

    // InventoryService 3xxx
    public const int StockReserved = 3001;
    public const int InsufficientStock = 3003;
    public const int ReservationReleased = 3004;
    public const int StockCacheRefreshed = 3010;
    public const int GcPause = 3011;
    public const int MemoryUsageHigh = 3100;
    public const int ProcessCrashed = 3101;
    public const int InstanceRestarting = 3102;
    public const int HealthCheckFailed = 3103;
    public const int InstanceRecovered = 3104;
    public const int StockRecountStarted = 3200;
    public const int LockWait = 3201;
    public const int RequestSurge = 3202;
    public const int ThreadPoolStarvation = 3203;
    public const int StockRecountCompleted = 3204;
    public const int HealthCheckTimeout = 3206;

    // PaymentService 4xxx
    public const int ServiceDeployed = 4000;
    public const int PaymentProcessing = 4001;
    public const int PaymentAuthorized = 4002;
    public const int PaymentDeclined = 4003;
    public const int GatewaySlowResponse = 4005;
    public const int SlowQuery = 4010;
    public const int GatewayCertificateExpiring = 4011;
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
    public const int ConnectionHeldTooLong = 4205;

    // NotificationService 5xxx
    public const int NotificationDeployed = 5000;
    public const int EmailQueued = 5001;
    public const int EmailDelivered = 5002;
    public const int EmailBounced = 5003;
    public const int SmtpConnectionRecycled = 5010;
    public const int TemplateRenderWarning = 5101;
    public const int SmtpTimeout = 5201;
    public const int ConsumerLag = 5202;
    public const int WorkersSaturated = 5203;
    public const int SmtpRecovered = 5204;
    public const int EmailSendFailed = 5205;

    // CatalogService 6xxx
    public const int CategoryListed = 6001;
    public const int SearchExecuted = 6002;
    public const int ProductViewed = 6003;
    public const int ProductNotFound = 6004;

    // CartService 7xxx
    public const int CartItemAdded = 7001;
    public const int CartItemRemoved = 7002;
    public const int CartViewed = 7003;
}
