using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIUsageBar.Core;

/// <summary>
/// Lenient JSON reading. None of the endpoints are public: fields appear,
/// disappear and change type, so every read is optional and never throws.
/// Each lookup accepts several key spellings (camelCase and snake_case).
/// </summary>
internal static partial class Json
{
    public static JsonNode? Parse(byte[] data)
    {
        try { return JsonNode.Parse(data); }
        catch { return null; }
    }

    private static JsonNode? Get(JsonNode? node, string[] keys)
    {
        if (node is not JsonObject obj) return null;
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var value) && value is not null) return value;
        }
        return null;
    }

    public static JsonObject? Obj(JsonNode? node, params string[] keys) => Get(node, keys) as JsonObject;

    public static JsonArray? Arr(JsonNode? node, params string[] keys) => Get(node, keys) as JsonArray;

    public static string? Str(JsonNode? node, params string[] keys) =>
        Get(node, keys) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool? Bool(JsonNode? node, params string[] keys) =>
        Get(node, keys) is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    /// <summary>A number, or a number sent as a string (proto3 int64).</summary>
    public static double? Num(JsonNode? node, params string[] keys)
    {
        if (Get(node, keys) is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<string>(out var text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    /// <summary>An RFC 3339 string, or epoch seconds/milliseconds.</summary>
    public static DateTimeOffset? Date(JsonNode? node, params string[] keys)
    {
        if (Get(node, keys) is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text))
        {
            return ParseDate(text) ?? (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? Epoch(n) : null);
        }
        return value.TryGetValue<double>(out var number) ? Epoch(number) : null;
    }

    /// <summary>
    /// ISO 8601 with up to nanosecond precision (more than .NET accepts), e.g.
    /// <c>2026-11-10T16:50:00.554353+00:00</c> or <c>…00.123456789Z</c>.
    /// </summary>
    public static DateTimeOffset? ParseDate(string text)
    {
        var trimmed = LongFraction().Replace(text, match => match.Value[..8]);
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
    }

    private static DateTimeOffset? Epoch(double value) => value <= 0
        ? null
        : value > 100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value)
            : DateTimeOffset.FromUnixTimeMilliseconds((long)(value * 1000));

    [GeneratedRegex(@"\.\d{8,}")]
    private static partial Regex LongFraction();
}
