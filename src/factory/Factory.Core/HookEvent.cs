using System.Text.Json;
using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>
/// The plugin's SubagentStart / SubagentStop hook (contract, "factory commands", <c>event</c> alone): the event a hook
/// payload appends, or null when it appends none.
/// </summary>
public static class HookEvent
{
    /// <summary>
    /// <c>agent-start</c> or <c>agent-stop</c> for <paramref name="job"/>, else <paramref name="idea"/>; null with neither, for
    /// an internal agent (an empty <c>agent_type</c>), another hook, or a payload that is not JSON. The plugin's
    /// <c>factory:</c> prefix is stripped, and agent-start records the agent id and its live transcript.
    /// </summary>
    public static JsonObject? From(string payload, string? job, string? idea)
    {
        if (string.IsNullOrEmpty(job) && string.IsNullOrEmpty(idea)) return null;
        JsonObject? p;
        try { p = JsonNode.Parse(payload) as JsonObject; }
        catch (JsonException) { return null; }
        if (p is null) return null;
        var agent = Str(p, "agent_type") is { } type && type.StartsWith("factory:", StringComparison.Ordinal) ? type["factory:".Length..] : Str(p, "agent_type");
        if (string.IsNullOrEmpty(agent)) return null;
        var kind = Str(p, "hook_event_name") switch { "SubagentStart" => "agent-start", "SubagentStop" => "agent-stop", _ => null };
        if (kind is null) return null;

        var e = new JsonObject();
        if (!string.IsNullOrEmpty(job)) e["job"] = job; else e["idea"] = idea;
        e["kind"] = kind;
        e["agent"] = agent;
        if (kind == "agent-start" && Str(p, "agent_id") is { Length: > 0 } id && Str(p, "transcript_path") is { Length: > 0 } transcript)
        {
            e["agentId"] = id;
            var session = transcript.EndsWith(".jsonl", StringComparison.Ordinal) ? transcript[..^".jsonl".Length] : transcript;
            e["transcript"] = Path.GetFullPath(Path.Combine(session, "subagents", $"agent-{id}.jsonl"));
        }
        return e;
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
