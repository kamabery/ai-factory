using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>
/// JSON written exactly as PowerShell 7's <c>ConvertTo-Json</c> writes it (the contract's output format):
/// two-space indents, <c>"key": value</c>, empty containers as <c>[]</c> and <c>{}</c>, numbers as read,
/// and only control characters, quote, backslash, U+0085, U+2028 and U+2029 escaped.
/// </summary>
public static class PsJson
{
    public static string Write(JsonNode? node, string newLine)
    {
        var sb = new StringBuilder();
        Append(sb, node, newLine, 0);
        return sb.ToString();
    }

    /// <summary><c>ConvertTo-Json -Compress</c>, as events.jsonl lines are written: no whitespace at all.</summary>
    public static string WriteCompact(JsonNode? node)
    {
        var sb = new StringBuilder();
        AppendCompact(sb, node);
        return sb.ToString();
    }

    private static void AppendCompact(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in o)
                {
                    if (!first) sb.Append(',');
                    AppendString(sb, key);
                    sb.Append(':');
                    AppendCompact(sb, value);
                    first = false;
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendCompact(sb, a[i]);
                }
                sb.Append(']');
                break;
            default:
                AppendScalar(sb, node);
                break;
        }
    }

    private static void Append(StringBuilder sb, JsonNode? node, string nl, int depth)
    {
        switch (node)
        {
            case JsonObject o when o.Count == 0:
                sb.Append("{}");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in o)
                {
                    sb.Append(first ? "" : ",").Append(nl).Append(' ', (depth + 1) * 2);
                    AppendString(sb, key);
                    sb.Append(": ");
                    Append(sb, value, nl, depth + 1);
                    first = false;
                }
                sb.Append(nl).Append(' ', depth * 2).Append('}');
                break;
            case JsonArray a when a.Count == 0:
                sb.Append("[]");
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    sb.Append(i == 0 ? "" : ",").Append(nl).Append(' ', (depth + 1) * 2);
                    Append(sb, a[i], nl, depth + 1);
                }
                sb.Append(nl).Append(' ', depth * 2).Append(']');
                break;
            default:
                AppendScalar(sb, node);
                break;
        }
    }

    private static void AppendScalar(StringBuilder sb, JsonNode? node)
    {
        if (node is null) sb.Append("null");
        else if (node.GetValueKind() == JsonValueKind.String) AppendString(sb, node.GetValue<string>());
        else sb.Append(node.ToJsonString()); // numbers as written, true, false
    }

    private static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ' or '\x85' or '\x2028' or '\x2029':
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }
}
