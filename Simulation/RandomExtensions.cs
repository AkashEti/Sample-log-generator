namespace SampleLogGenerator.Simulation;

internal static class RandomExtensions
{
    public static int Between(this Random rng, int min, int max) => rng.Next(min, max + 1);

    public static double Between(this Random rng, double min, double max) => min + rng.NextDouble() * (max - min);

    public static bool Chance(this Random rng, double probability) => rng.NextDouble() < probability;

    public static T Pick<T>(this Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    public static T Weighted<T>(this Random rng, params (T Item, int Weight)[] choices)
    {
        var roll = rng.Next(choices.Sum(c => c.Weight));
        foreach (var (item, weight) in choices)
        {
            if (roll < weight) return item;
            roll -= weight;
        }
        return choices[^1].Item;
    }

    /// <summary>Inter-arrival time of a Poisson process with the given rate.</summary>
    public static TimeSpan Exponential(this Random rng, double ratePerSecond) =>
        TimeSpan.FromSeconds(-Math.Log(1 - rng.NextDouble()) / ratePerSecond);

    /// <summary>A version-4 style GUID drawn from the seeded generator, so seeded runs are reproducible.</summary>
    public static Guid NextGuid(this Random rng)
    {
        Span<byte> bytes = stackalloc byte[16];
        rng.NextBytes(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public static string Hex(this Random rng, int length)
    {
        Span<byte> bytes = stackalloc byte[(length + 1) / 2];
        rng.NextBytes(bytes);
        return Convert.ToHexStringLower(bytes)[..length];
    }
}
