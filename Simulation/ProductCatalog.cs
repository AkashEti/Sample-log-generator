namespace SampleLogGenerator.Simulation;

internal sealed record Product(string Sku, string Name, string Category, decimal Price);

/// <summary>A fixed catalog of fake products. Built from its own seed so it is identical in every run.</summary>
internal static class ProductCatalog
{
    private static readonly (string Category, string[] Lines, int MinPrice, int MaxPrice)[] Categories =
    [
        ("laptops", ["Zenbook", "ThinkPad", "XPS", "Aero"], 700, 2400),
        ("phones", ["Pixel", "Galaxy", "Nova", "Edge"], 300, 1300),
        ("headphones", ["QuietComfort", "WH-1000", "Studio", "Buds"], 60, 400),
        ("monitors", ["UltraSharp", "Odyssey", "ProArt", "Nitro"], 150, 900),
        ("keyboards", ["MX Keys", "K2", "Blade", "Aurora"], 40, 220),
        ("cameras", ["Alpha", "EOS", "Z", "X-T"], 450, 2500),
        ("smart-home", ["Nest Hub", "Echo", "Hue Bridge", "Ring"], 30, 250),
        ("gaming", ["Switch", "DualSense", "Quest", "Deck"], 50, 600),
    ];

    public static IReadOnlyList<string> CategoryNames { get; } = [.. Categories.Select(c => c.Category)];

    public static IReadOnlyList<string> SearchTerms { get; } =
    [
        "wireless headphones", "gaming laptop", "4k monitor", "mechanical keyboard", "pixel", "galaxy",
        "usb-c hub", "noise cancelling", "mirrorless camera", "smart bulb", "thinkpad", "vr headset", "webcam",
    ];

    public static IReadOnlyList<Product> Products { get; } = Build();

    public static IReadOnlyList<Product> InCategory(string category) => [.. Products.Where(p => p.Category == category)];

    private static List<Product> Build()
    {
        var rng = new Random(20260926);
        var products = new List<Product>();
        var sku = 1000;
        foreach (var (category, lines, minPrice, maxPrice) in Categories)
        {
            for (var i = 0; i < 12; i++)
            {
                var name = $"{lines[i % lines.Length]} {rng.Next(2, 9)}{(char)('A' + rng.Next(0, 6))}{rng.Next(0, 10)}";
                var price = Math.Round((decimal)(minPrice + rng.NextDouble() * (maxPrice - minPrice)), 0) - 0.01m;
                products.Add(new Product($"SKU-{sku++}", name, category, price));
            }
        }
        return products;
    }
}
