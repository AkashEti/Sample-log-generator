namespace SampleLogGenerator.Simulation;

/// <summary>Identity and state of one checkout request as it travels across services.</summary>
internal sealed class OrderContext
{
    private readonly Dictionary<string, string> _requestIds = [];
    private readonly Dictionary<string, string> _hosts = [];

    public required string OrderId { get; init; }
    public required string CorrelationId { get; init; }
    public required string CustomerId { get; init; }
    public required string PaymentMethod { get; init; }
    public required string Sku { get; init; }
    public required int ItemCount { get; init; }
    public required decimal Amount { get; init; }

    /// <summary>The order id only appears in logs once OrderService has created the order.</summary>
    public bool IsCreated { get; set; }

    /// <summary>Each service handling the request gets its own request id.</summary>
    public string RequestIdFor(string service, Random rng)
    {
        if (!_requestIds.TryGetValue(service, out var id))
            _requestIds[service] = id = "req-" + rng.Hex(12);
        return id;
    }

    /// <summary>A request sticks to one instance per service.</summary>
    public string HostFor(string service, Random rng)
    {
        if (!_hosts.TryGetValue(service, out var host))
            _hosts[service] = host = rng.Pick(Services.HostsFor(service));
        return host;
    }

    public void PinHost(string service, string host) => _hosts[service] = host;
}

/// <summary>
/// Writes the log lines of one order flow in time order. <see cref="Wait(int)"/> moves the
/// cursor forward; each log line is stamped with the current cursor time.
/// </summary>
internal sealed class Flow(LogGenerator generator, OrderContext order, DateTime start)
{
    public OrderContext Order { get; } = order;
    public DateTime Cursor { get; private set; } = start;
    public Random Rng => generator.Rng;

    public Flow Wait(int milliseconds)
    {
        Cursor = Cursor.AddMilliseconds(milliseconds);
        return this;
    }

    public Flow Wait(int minMilliseconds, int maxMilliseconds) => Wait(Rng.Between(minMilliseconds, maxMilliseconds));

    public Flow Info(string service, int eventId, string message, int? durationMs = null) =>
        Log(Levels.Information, service, eventId, message, null, durationMs);

    public Flow Warning(string service, int eventId, string message, string? exception = null, int? durationMs = null) =>
        Log(Levels.Warning, service, eventId, message, exception, durationMs);

    public Flow Error(string service, int eventId, string message, string? exception = null, int? durationMs = null) =>
        Log(Levels.Error, service, eventId, message, exception, durationMs);

    public Flow Log(string level, string service, int eventId, string message, string? exception, int? durationMs)
    {
        generator.Emit(Cursor, level, service, eventId, message, Order, exception, durationMs);
        return this;
    }
}
