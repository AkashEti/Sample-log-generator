using SampleLogGenerator.Configuration;

namespace SampleLogGenerator.Simulation;

internal enum UserAction
{
    Browse,
    Search,
    ViewProduct,
    AddToCart,
    RemoveFromCart,
    ViewCart,
    Checkout,
    Leave,
}

/// <summary>
/// A population of concurrent visitors. The target size is re-rolled within
/// [<see cref="LogGeneratorOptions.MinConcurrentUsers"/>, <see cref="LogGeneratorOptions.MaxConcurrentUsers"/>] every few minutes.
/// Visitors arrive to fill the gap and leave on their own, so the live count drifts toward the target.
///
/// Each visitor is a session walking a simple journey (browse, search, view products, cart, checkout), with think
/// time between actions. All sessions share one simulation clock, so their requests interleave in the logs just like
/// parallel traffic, while seeded runs stay reproducible.
/// </summary>
internal sealed class UserTraffic(LogGenerator generator, LogGeneratorOptions options)
{
    private sealed class Session
    {
        public required string SessionId { get; init; }
        public required string CartId { get; set; }
        public string? UserId { get; set; }
        public bool SignInOnArrival { get; set; }
        public string? Category { get; set; }
        public Product? LastViewed { get; set; }
        public List<(Product Product, int Quantity)> Cart { get; } = [];
        public UserAction Next { get; set; }
        public int ActionsLeft { get; set; }
    }

    private static readonly string[] SignInMethods = ["password", "password", "google", "apple"];

    private readonly PriorityQueue<Session, (DateTime At, long Sequence)> _sessions = new();
    private long _sequence;
    private DateTime _retargetAt;

    public int ActiveUsers => _sessions.Count;
    public int TargetUsers { get; private set; }
    private Random Rng => generator.Rng;
    private int MinUsers => Math.Max(1, options.MinConcurrentUsers);
    private int MaxUsers => Math.Max(MinUsers, options.MaxConcurrentUsers);

    public void Start(DateTime now)
    {
        Retarget(now);
        Arrive(now, spreadSeconds: 15);
    }

    public void Advance(DateTime now)
    {
        while (_retargetAt <= now) Retarget(_retargetAt);
        Arrive(now, spreadSeconds: 60);

        while (_sessions.TryPeek(out var session, out var due) && due.At <= now)
        {
            _sessions.Dequeue();
            Act(session, due.At);
        }

        // Replace visitors who just left so the population never dips below the target.
        Arrive(now, spreadSeconds: 60);
    }

    /// <summary>After a pause, move overdue sessions to "now" instead of replaying the gap.</summary>
    public void Resume(DateTime now)
    {
        var sessions = new List<(Session, DateTime)>();
        while (_sessions.TryDequeue(out var session, out var due))
            sessions.Add((session, due.At < now ? now.AddMilliseconds(Rng.Between(0, 10000)) : due.At));
        foreach (var (session, at) in sessions) Schedule(session, at);
        if (_retargetAt < now) Retarget(now);
    }

    private void Retarget(DateTime at)
    {
        TargetUsers = Rng.Between(MinUsers, MaxUsers);
        _retargetAt = at.AddSeconds(Rng.Between(120, 360));
    }

    /// <summary>New visitors fill the gap to the target, arriving spread out rather than all at once.</summary>
    private void Arrive(DateTime now, int spreadSeconds)
    {
        for (var i = ActiveUsers; i < TargetUsers; i++)
        {
            var session = new Session
            {
                SessionId = "sess-" + Rng.Hex(10),
                CartId = "cart-" + Rng.Hex(8),
                SignInOnArrival = Rng.Chance(0.45), // returning customers sign in first
                Next = Rng.Chance(0.65) ? UserAction.Browse : UserAction.Search,
                ActionsLeft = Rng.Between(4, 30),
            };
            Schedule(session, now.AddMilliseconds(Rng.Between(0, spreadSeconds * 1000)));
        }
    }

    private void Schedule(Session session, DateTime at) => _sessions.Enqueue(session, (at, _sequence++));

    private RequestContext NewRequest(Session s) =>
        new() { CorrelationId = Rng.NextGuid().ToString(), SessionId = s.SessionId, UserId = s.UserId };

    private void Act(Session s, DateTime at)
    {
        if (s.SignInOnArrival)
        {
            s.SignInOnArrival = false;
            at = SignIn(s, at).AddMilliseconds(Rng.Between(300, 2000));
        }

        var action = s.Next;
        if (action is UserAction.AddToCart && s.LastViewed is null) action = UserAction.ViewProduct;
        if (action is UserAction.RemoveFromCart or UserAction.ViewCart or UserAction.Checkout && s.Cart.Count == 0) action = UserAction.Browse;

        switch (action)
        {
            case UserAction.Browse:
            {
                s.Category = Rng.Pick(ProductCatalog.CategoryNames);
                var page = Rng.Chance(0.7) ? 1 : Rng.Between(2, 4);
                var ms = Rng.Between(20, 140);
                generator.Emit(at, Levels.Information, Services.Catalog, EventIds.CategoryListed,
                    $"Listed category '{s.Category}' page {page}: {ProductCatalog.InCategory(s.Category).Count} products", NewRequest(s), durationMs: ms);
                break;
            }
            case UserAction.Search:
            {
                var term = Rng.Pick(ProductCatalog.SearchTerms);
                var results = Rng.Chance(0.06) ? 0 : Rng.Between(1, 60);
                var ms = Rng.Between(30, 260);
                generator.Emit(at, Levels.Information, Services.Catalog, EventIds.SearchExecuted,
                    $"Search '{term}' returned {results} results", NewRequest(s), durationMs: ms);
                break;
            }
            case UserAction.ViewProduct:
            {
                var ms = Rng.Between(15, 110);
                if (Rng.Chance(0.015))
                {
                    // A stale link: harmless noise.
                    generator.Emit(at, Levels.Warning, Services.Catalog, EventIds.ProductNotFound,
                        $"Product SKU-{Rng.Between(9000, 9999)} not found (404)", NewRequest(s), durationMs: ms);
                    break;
                }

                var product = s.Category is not null && Rng.Chance(0.6)
                    ? Rng.Pick(ProductCatalog.InCategory(s.Category))
                    : Rng.Pick(ProductCatalog.Products);
                s.LastViewed = product;
                generator.Emit(at, Levels.Information, Services.Catalog, EventIds.ProductViewed,
                    $"Product {product.Sku} viewed: {product.Name} ({product.Price:F2} USD)", NewRequest(s), durationMs: ms);
                break;
            }
            case UserAction.AddToCart:
            {
                var product = s.LastViewed!;
                var quantity = Rng.Chance(0.85) ? 1 : 2;
                var index = s.Cart.FindIndex(line => line.Product.Sku == product.Sku);
                if (index >= 0) s.Cart[index] = (product, s.Cart[index].Quantity + quantity);
                else s.Cart.Add((product, quantity));
                generator.Emit(at, Levels.Information, Services.Cart, EventIds.CartItemAdded,
                    $"Added {quantity} x {product.Sku} to cart {s.CartId} ({s.Cart.Count} lines)", NewRequest(s), durationMs: Rng.Between(8, 45));
                break;
            }
            case UserAction.RemoveFromCart:
            {
                var line = Rng.Pick(s.Cart);
                s.Cart.Remove(line);
                generator.Emit(at, Levels.Information, Services.Cart, EventIds.CartItemRemoved,
                    $"Removed {line.Product.Sku} from cart {s.CartId} ({s.Cart.Count} lines)", NewRequest(s), durationMs: Rng.Between(8, 40));
                break;
            }
            case UserAction.ViewCart:
                generator.Emit(at, Levels.Information, Services.Cart, EventIds.CartViewed,
                    $"Cart {s.CartId} viewed: {s.Cart.Sum(l => l.Quantity)} items, total {s.Cart.Sum(l => l.Product.Price * l.Quantity):F2} USD",
                    NewRequest(s), durationMs: Rng.Between(10, 60));
                break;
            case UserAction.Checkout:
                if (s.UserId is null) at = SignIn(s, at); // guests sign in to check out
                generator.Checkout(at.AddMilliseconds(Rng.Between(300, 1500)), s.SessionId, s.UserId!, [.. s.Cart]);
                s.Cart.Clear();
                s.CartId = "cart-" + Rng.Hex(8);
                break;
            case UserAction.Leave:
                if (s.UserId is not null && Rng.Chance(0.25))
                    generator.Emit(at, Levels.Information, Services.Auth, EventIds.UserSignedOut, $"User {s.UserId} signed out", NewRequest(s));
                return; // the session is over
        }

        s.ActionsLeft--;
        s.Next = s.ActionsLeft <= 0 ? UserAction.Leave : NextAction(action);
        var thinkMs = s.Next == UserAction.Leave ? Rng.Between(1000, 5000) : Rng.Between(2000, 12000);
        Schedule(s, at.AddMilliseconds(thinkMs));
    }

    /// <returns>The time the user is signed in.</returns>
    private DateTime SignIn(Session s, DateTime at)
    {
        var userId = $"CUST-{Rng.Between(10000, 99999)}";
        if (Rng.Chance(0.03))
        {
            // A mistyped password, then a successful retry.
            generator.Emit(at, Levels.Warning, Services.Auth, EventIds.SignInFailed,
                $"Sign-in failed for user {userId}: invalid password", NewRequest(s), durationMs: Rng.Between(40, 200));
            at = at.AddMilliseconds(Rng.Between(3000, 9000));
        }

        var ms = Rng.Between(40, 250);
        at = at.AddMilliseconds(ms);
        s.UserId = userId;
        generator.Emit(at, Levels.Information, Services.Auth, EventIds.UserSignedIn,
            $"User {userId} signed in via {Rng.Pick(SignInMethods)}", NewRequest(s), durationMs: ms);
        return at;
    }

    private UserAction NextAction(UserAction current) => current switch
    {
        UserAction.Browse => Rng.Weighted((UserAction.Browse, 20), (UserAction.Search, 15), (UserAction.ViewProduct, 55), (UserAction.Leave, 10)),
        UserAction.Search => Rng.Weighted((UserAction.ViewProduct, 60), (UserAction.Search, 20), (UserAction.Browse, 10), (UserAction.Leave, 10)),
        UserAction.ViewProduct => Rng.Weighted((UserAction.AddToCart, 30), (UserAction.ViewProduct, 25), (UserAction.Browse, 15), (UserAction.Search, 15), (UserAction.Leave, 15)),
        UserAction.AddToCart => Rng.Weighted((UserAction.Checkout, 35), (UserAction.ViewCart, 25), (UserAction.ViewProduct, 20), (UserAction.Browse, 10), (UserAction.Leave, 10)),
        UserAction.RemoveFromCart => Rng.Weighted((UserAction.ViewCart, 40), (UserAction.ViewProduct, 30), (UserAction.Leave, 30)),
        UserAction.ViewCart => Rng.Weighted((UserAction.Checkout, 50), (UserAction.RemoveFromCart, 10), (UserAction.Browse, 20), (UserAction.Leave, 20)),
        UserAction.Checkout => Rng.Weighted((UserAction.Browse, 20), (UserAction.Leave, 80)),
        _ => UserAction.Leave,
    };
}
