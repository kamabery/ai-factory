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

/// <summary>The contract's times: UTC, milliseconds, <c>Z</c> (factory.ps1's <c>Now</c>).</summary>
public static class Iso
{
    public static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

/// <summary>Stages, roles and the Building column (contract, "Board columns and stages").</summary>
public static class Stages
{
    public const string Backlog = "backlog", Approved = "approved", Build = "build", Review = "review", Deploy = "deploy",
        Approve = "approve", Gate = "gate", Blocked = "blocked", Ready = "ready", Done = "done", Discarded = "discarded";

    public static readonly string[] All = [Backlog, Approved, Build, Review, Deploy, Approve, Gate, Blocked, Ready, Done, Discarded];

    /// <summary>The stages that count as building: one job at a time. <c>blocked</c> is in the column but not here.</summary>
    public static readonly string[] Building = [Build, Review, Deploy, Approve, Gate];

    public static readonly string[] Roles = ["security", "code", "ux", "ui"];
}

/// <summary>A job on the board: a typed view over its JSON object, which keeps the file's keys and order.</summary>
public sealed class Job(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string Id => Json.Text(Node["id"]);
    public string? Idea => Json.Str(Node["idea"]);
    public string Stage => Json.Text(Node["stage"]);
    public int Round => Json.Int(Node["round"]);
    public string? Kind => Json.Str(Node["kind"]);
    public bool IsBootstrap => Json.Is(Kind, "bootstrap");
    public string? Target => Json.Str(Node["target"]);
    public string? Base => Json.Str(Node["base"]);
    public string? ApprovedAt => Json.Str(Node["approvedAt"]);
    public string? Resume => Json.Str(Node["resume"]);
    public Flag? Flag => Node["flag"] is JsonObject f ? new Flag(f) : null;
    public Deploy? Deploy => Node["deploy"] is JsonObject d ? new Deploy(d) : null;
}

/// <summary><c>flag</c>: why a ready or blocked job stopped.</summary>
public sealed class Flag(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string Reason => Json.Text(Node["reason"]);
    public string? Detail => Json.Str(Node["detail"]);
    public string? Why => Json.Str(Node["why"]);
    /// <summary>usage-limit only: the Building stage claude stopped in.</summary>
    public string? Stage => Json.Str(Node["stage"]);
    /// <summary>usage-limit only: claude's session id.</summary>
    public string? Session => Json.Str(Node["session"]);
}

/// <summary><c>deploy</c>: the job's preview.</summary>
public sealed class Deploy(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string? Status => Json.Str(Node["status"]);
    public int Attempts => Json.Int(Node["attempts"]);
    public string? DestroyStartedAt => Json.Str(Node["destroyStartedAt"]);
    public int DestroyFailures => Json.Int(Node["destroyFailures"]);
}

/// <summary>An idea on the board.</summary>
public sealed class Idea(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string Id => Json.Text(Node["id"]);
    public string? Status => Json.Str(Node["status"]);
    public string? Target => Json.Str(Node["target"]);
}

/// <summary>Builds events.jsonl lines: <c>ts</c> first, then the fields in the order given.</summary>
public static class Events
{
    /// <summary>The contract's event kinds (factory.ps1's <c>$EventKinds</c>).</summary>
    public static readonly string[] Kinds =
        ["idea", "stage", "agent-start", "agent-stop", "gate", "merge", "cost", "note", "eval", "feedback", "deploy", "destroy", "lesson", "canary"];

    public static JsonObject Create(DateTimeOffset now, params (string Key, JsonNode? Value)[] fields)
    {
        var e = new JsonObject { ["ts"] = Iso.Format(now) };
        foreach (var (k, v) in fields) e[k] = v;
        return e;
    }

    public static void RequireKind(string? kind)
    {
        if (!Json.In(kind, Kinds)) throw new FactoryException($"unknown event kind '{kind}'");
    }
}
