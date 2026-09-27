namespace SampleLogGenerator.Simulation;

/// <summary>
/// The healthy checkout path, split into steps so incident scenarios can replace individual steps.
/// It includes a small amount of normal business noise (declines, out-of-stock, slow responses, bounces)
/// so that not every Warning points to an incident.
/// </summary>
internal static class CommonFlows
{
    private static readonly string[] DeclineReasons = ["insufficient funds", "card expired", "do not honor", "suspected fraud"];

    public static void Normal(Flow f)
    {
        Checkout(f);
        CreateOrder(f);
        if (!ReserveInventory(f)) return;
        if (!ProcessPayment(f)) return;
        ConfirmAndNotify(f);
    }

    public static void Checkout(Flow f)
    {
        var o = f.Order;
        f.Info(Services.Order, EventIds.CheckoutReceived, $"Checkout request received for customer {o.CustomerId} ({o.ItemCount} items)");
        var ms = f.Rng.Between(3, 18);
        f.Wait(ms).Info(Services.Auth, EventIds.TokenValidated, $"Access token validated for customer {o.CustomerId}", ms);
    }

    public static void CreateOrder(Flow f)
    {
        var o = f.Order;
        o.IsCreated = true;
        f.Wait(8, 30).Info(Services.Order, EventIds.OrderCreated,
            $"Order {o.OrderId} created for customer {o.CustomerId}: {o.ItemCount} items, total {o.Amount:F2} USD");
    }

    public static bool ReserveInventory(Flow f)
    {
        var o = f.Order;
        var ms = f.Rng.Between(15, 90);
        f.Wait(ms);
        if (f.Rng.Chance(0.02))
        {
            f.Warning(Services.Inventory, EventIds.InsufficientStock, $"Insufficient stock for {o.Sku} requested by order {o.OrderId}", durationMs: ms)
             .Wait(5, 20)
             .Info(Services.Order, EventIds.OrderCancelledOutOfStock, $"Order {o.OrderId} cancelled: item {o.Sku} out of stock");
            return false;
        }

        f.Info(Services.Inventory, EventIds.StockReserved, $"Reserved {o.ItemCount} items for order {o.OrderId}", ms);
        return true;
    }

    public static void StartPayment(Flow f)
    {
        var o = f.Order;
        f.Wait(5, 25).Info(Services.Payment, EventIds.PaymentProcessing,
            $"Processing payment for order {o.OrderId}: {o.Amount:F2} USD via {o.PaymentMethod}");
    }

    public static bool ProcessPayment(Flow f)
    {
        var o = f.Order;
        StartPayment(f);

        if (f.Rng.Chance(0.03))
        {
            var declineMs = f.Rng.Between(200, 700);
            f.Wait(declineMs)
             .Warning(Services.Payment, EventIds.PaymentDeclined, $"Payment declined for order {o.OrderId}: {f.Rng.Pick(DeclineReasons)}", durationMs: declineMs)
             .Wait(5, 20)
             .Info(Services.Order, EventIds.OrderCancelledPaymentDeclined, $"Order {o.OrderId} cancelled: payment declined");
            ReleaseInventory(f);
            return false;
        }

        int duration;
        if (f.Rng.Chance(0.02))
        {
            // An isolated slow response: a red herring, not an incident.
            duration = f.Rng.Between(2500, 4500);
            f.Wait(2000).Warning(Services.Payment, EventIds.GatewaySlowResponse, $"Payment gateway response slow for order {o.OrderId} (2000 ms elapsed)");
            f.Wait(duration - 2000);
        }
        else
        {
            duration = f.Rng.Between(150, 650);
            f.Wait(duration);
        }

        f.Info(Services.Payment, EventIds.PaymentAuthorized, $"Payment authorized for order {o.OrderId} (transaction txn_{f.Rng.Hex(10)})", duration);
        return true;
    }

    public static void ReleaseInventory(Flow f) =>
        f.Wait(10, 40).Info(Services.Inventory, EventIds.ReservationReleased, $"Released inventory reservation for order {f.Order.OrderId}");

    public static void ConfirmAndNotify(Flow f)
    {
        var o = f.Order;
        f.Wait(5, 20).Info(Services.Order, EventIds.OrderConfirmed, $"Order {o.OrderId} confirmed");
        f.Wait(10, 60).Info(Services.Notification, EventIds.EmailQueued, $"Order confirmation email queued for order {o.OrderId}");

        var ms = f.Rng.Between(120, 900);
        f.Wait(ms);
        if (f.Rng.Chance(0.01))
            f.Warning(Services.Notification, EventIds.EmailBounced, $"Confirmation email for order {o.OrderId} bounced: mailbox unavailable (550)", durationMs: ms);
        else
            f.Info(Services.Notification, EventIds.EmailDelivered, $"Confirmation email delivered for order {o.OrderId}", ms);
    }
}
