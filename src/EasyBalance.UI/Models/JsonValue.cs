using System.Globalization;
using System.Text.Json;

namespace EasyBalance.UI.Models;

public static class JsonValue
{
    public static JsonElement Property(JsonElement source, params string[] names)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        foreach (var property in source.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return property.Value;
            }
        }

        return default;
    }

    public static string String(JsonElement source, string fallback = "", params string[] names)
    {
        var value = Property(source, names);
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? fallback;
        }

        if (value.ValueKind == JsonValueKind.Number || value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.ToString();
        }

        return fallback;
    }

    public static bool Bool(JsonElement source, bool fallback = false, params string[] names)
    {
        var value = Property(source, names);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            _ => fallback
        };
    }

    public static int Int32(JsonElement source, int fallback = 0, params string[] names)
    {
        var value = Property(source, names);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : fallback;
    }

    public static long Int64(JsonElement source, long fallback = 0, params string[] names)
    {
        var value = Property(source, names);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : fallback;
    }

    public static double Double(JsonElement source, double fallback = 0, params string[] names)
    {
        var value = Property(source, names);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : fallback;
    }

    public static double Milliseconds(JsonElement source, string name)
    {
        var value = Property(source, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var milliseconds))
        {
            return milliseconds;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            if (TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var duration))
            {
                return duration.TotalMilliseconds;
            }

            if (double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds))
            {
                return milliseconds;
            }
        }

        return -1;
    }

    public static int Seconds(JsonElement source, int fallback, string name)
    {
        var value = Property(source, name);
        if (value.ValueKind == JsonValueKind.String && TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var duration))
        {
            return Math.Max(0, (int)Math.Round(duration.TotalSeconds));
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var seconds))
        {
            return Math.Max(0, seconds);
        }

        return fallback;
    }

    public static Guid Guid(JsonElement source, params string[] names)
    {
        var text = String(source, string.Empty, names);
        return System.Guid.TryParse(text, out var value) ? value : System.Guid.Empty;
    }

    public static DateTimeOffset? DateTimeOffset(JsonElement source, params string[] names)
    {
        var value = Property(source, names);
        if (value.ValueKind == JsonValueKind.String && System.DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static string StringList(JsonElement source, params string[] names)
    {
        var value = Property(source, names);
        if (value.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(", ", value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    public static IEnumerable<JsonElement> Items(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Array)
        {
            return source.EnumerateArray().ToArray();
        }

        return Array.Empty<JsonElement>();
    }

    public static string DisplayValue(JsonElement source, params string[] names)
    {
        var value = Property(source, names);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "—";
        }

        return value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.Array
            ? value.GetRawText()
            : value.ToString();
    }
}
