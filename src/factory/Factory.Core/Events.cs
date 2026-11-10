using System.Text.Json.Nodes;

namespace Factory.Core;

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
        if (!PsJsonReader.In(kind, Kinds)) throw new FactoryException($"unknown event kind '{kind}'");
    }
}
