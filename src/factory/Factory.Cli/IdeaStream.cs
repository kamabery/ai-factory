using System.Text.Json;
using System.Text.Json.Nodes;

namespace Factory.Cli;

/// <summary>
/// Reads one turn's <c>claude --output-format stream-json</c>: streamed text and each server tool call go to
/// stream.jsonl as they arrive, and the turn's messages are kept in the Messages API's shape for chat.json, with the
/// MCP prefix taken off tool names.
/// </summary>
public sealed class IdeaStream(Action<JsonObject> stream)
{
    public const string Prefix = "mcp__factory__";

    private readonly List<JsonObject> _messages = [];
    private readonly Dictionary<JsonObject, string> _ids = [];

    /// <summary>The result line, once claude wrote one.</summary>
    public JsonObject? Result { get; private set; }

    public void Accept(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonObject? e;
        try { e = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return; }
        switch (IdeaTools.Text(e?["type"]))
        {
            case "stream_event" when e!["event"] is JsonObject ev && IdeaTools.Text(ev["type"]) == "content_block_delta"
                                     && ev["delta"] is JsonObject d && IdeaTools.Text(d["type"]) == "text_delta":
                stream(new JsonObject { ["type"] = "text", ["text"] = IdeaTools.Text(d["text"]) });
                break;
            case "assistant" when e!["message"] is JsonObject m:
                Assistant(m);
                break;
            case "user" when e!["message"] is JsonObject m:
                User(m);
                break;
            case "result":
                Result = e;
                break;
        }
    }

    private void Assistant(JsonObject m)
    {
        var id = IdeaTools.Text(m["id"]) ?? "";
        var blocks = (m["content"] as JsonArray ?? []).OfType<JsonObject>().Select(b => (JsonObject)b.DeepClone()).ToList();
        foreach (var b in blocks.Where(b => IdeaTools.Text(b["type"]) == "tool_use"))
        {
            var name = IdeaTools.Text(b["name"]) ?? "";
            if (name.StartsWith(Prefix, StringComparison.Ordinal)) b["name"] = name = name[Prefix.Length..];
            if (!IdeaTools.UiTools.Contains(name))
            {
                var input = b["input"] as JsonObject;
                var summary = (IdeaTools.Text(input?["path"]) ?? IdeaTools.Text(input?["pattern"]) ?? "").Trim();
                stream(new JsonObject { ["type"] = "tool", ["name"] = name, ["summary"] = summary });
            }
        }
        // claude writes one assistant event per block; one API message shares its id.
        if (_messages.Count > 0 && _messages[^1] is var last && _ids.GetValueOrDefault(last) == id && id.Length > 0)
            foreach (var b in blocks) ((JsonArray)last["content"]!).Add(b);
        else
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray([.. blocks]) };
            _ids[message] = id;
            _messages.Add(message);
        }
    }

    private void User(JsonObject m)
    {
        var blocks = new List<JsonObject>();
        foreach (var b in (m["content"] as JsonArray ?? []).OfType<JsonObject>().Where(b => IdeaTools.Text(b["type"]) == "tool_result"))
        {
            var r = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = IdeaTools.Text(b["tool_use_id"]), ["content"] = IdeaPrompt.ResultText(b["content"]) };
            if (b["is_error"] is JsonValue v && v.GetValueKind() == JsonValueKind.True) r["is_error"] = true;
            blocks.Add(r);
        }
        if (blocks.Count == 0) return;
        if (_messages.Count > 0 && IdeaTools.Text(_messages[^1]["role"]) == "user")
            foreach (var b in blocks) ((JsonArray)_messages[^1]["content"]!).Add(b);
        else _messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray([.. blocks]) });
    }

    /// <summary>
    /// The turn's messages for chat.json. When the session has a pending UI call (the MCP server recorded it, without
    /// an id), it gets its tool_use id here; the placeholder result claude saw for it is dropped, and the results of the
    /// server tools beside it become <c>pendingResults</c>, which the human's answer carries into chat.json.
    /// </summary>
    public List<JsonObject> Finish(JsonObject session)
    {
        var messages = _messages.Select(m => (JsonObject)m.DeepClone()).ToList();
        if (session["pending"] is not JsonObject pending || IdeaTools.Text(pending["id"]) is { Length: > 0 }) return messages;
        var name = IdeaTools.Text(pending["name"]);
        var use = messages.Where(m => IdeaTools.Text(m["role"]) == "assistant").SelectMany(m => ((JsonArray)m["content"]!).OfType<JsonObject>())
            .LastOrDefault(b => IdeaTools.Text(b["type"]) == "tool_use" && IdeaTools.Text(b["name"]) == name);
        if (use is null) return messages;
        var id = IdeaTools.Text(use["id"]);
        pending["id"] = id;
        var holder = messages.FindIndex(m => IdeaTools.Text(m["role"]) == "user"
            && ((JsonArray)m["content"]!).OfType<JsonObject>().Any(b => IdeaTools.Text(b["tool_use_id"]) == id));
        if (holder < 0) return messages;
        var others = ((JsonArray)messages[holder]["content"]!).OfType<JsonObject>().Where(b => IdeaTools.Text(b["tool_use_id"]) != id);
        session["pendingResults"] = new JsonArray([.. others.Select(b => (JsonNode)b.DeepClone())]);
        messages.RemoveAt(holder);
        return messages;
    }
}
