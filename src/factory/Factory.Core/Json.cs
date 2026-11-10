using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>
/// Reads JSON as factory.ps1 reads its hashtables: a missing key is null, strings compare without case
/// (PowerShell's -eq, -in), and a value interpolates as PowerShell prints it.
/// </summary>
internal static class Json
{
    public static bool Is(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool In(string? a, params ReadOnlySpan<string> set)
    {
        foreach (var s in set) if (Is(a, s)) return true;
        return false;
    }

    /// <summary>The value as a string, or null when it is missing or null.</summary>
    public static string? Str(JsonNode? n) => n is null ? null : Text(n);

    /// <summary>The value as PowerShell interpolates it: missing or null is empty.</summary>
    public static string Text(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        _ => n.ToJsonString(),
    };

    /// <summary>PowerShell's <c>[int]</c>: null is 0, a number or numeric string is rounded.</summary>
    public static int Int(JsonNode? n) => n switch
    {
        null => 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => (int)Math.Round(Number(v), MidpointRounding.ToEven),
        _ => int.TryParse(Text(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0,
    };

    /// <summary>A number, whether it was parsed from a file or set from C# as an int, long or double.</summary>
    public static double Number(JsonValue v) => double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>PowerShell truthiness: null, "", false and 0 are false; any object or array is true.</summary>
    public static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Length > 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => Number(v) != 0,
        _ => true,
    };
}
