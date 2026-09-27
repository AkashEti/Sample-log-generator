using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SampleLogGenerator.Output;

public static class JsonDefaults
{
    /// <summary>camelCase, enums as strings, null fields omitted, fixed-width UTC timestamps.</summary>
    public static JsonSerializerOptions Options { get; } = Apply(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Apply(JsonSerializerOptions options)
    {
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }

    /// <summary>
    /// Always writes "yyyy-MM-ddTHH:mm:ss.fffZ" (the default converter trims trailing zeros),
    /// so timestamps sort correctly as plain strings in grep/sort/jq pipelines.
    /// </summary>
    private sealed class UtcTimestampConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}
