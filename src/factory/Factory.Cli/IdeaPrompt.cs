using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Cli;

/// <summary>The turn's system prompt and its prompt (contract, "Idea sessions", Turns).</summary>
public static partial class IdeaPrompt
{
    /// <summary>
    /// idea-turn.ps1's Get-SystemPrompt: the dashboard preamble, the grill skill, the spec-writer's rules and both
    /// templates, assembled at turn time so an edit to any of them shows on the next turn.
    /// </summary>
    public static string System(string factoryRoot)
    {
        string Read(params string[] rel) => File.ReadAllText(Path.Combine([factoryRoot, .. rel]));
        string Body(params string[] rel) => FrontMatter().Replace(Read(rel), "");
        return string.Join("\n",
            Body("factory", "plugin", "prompts", "idea-api.md"),
            "# The grill skill\n",
            Body("factory", "plugin", "skills", "grill", "SKILL.md"),
            "# The spec-writer's rules\n",
            Body("factory", "plugin", "agents", "spec-writer.md"),
            "# The brief template (factory/templates/brief.md)\n",
            Read("factory", "templates", "brief.md"),
            "# The spec template (factory/templates/spec.md)\n",
            Read("factory", "templates", "spec.md"),
            "# The product template (factory/templates/product.md)\n",
            Read("factory", "templates", "product.md"));
    }

    /// <summary>
    /// What this turn sends: on the first turn (no claude session yet) every user text so far; after that, the user
    /// content <c>chat.json</c> gained since the last assistant message, with an answer to a UI tool spelled out, and
    /// <c>Continue.</c> when there is none (a retry).
    /// </summary>
    public static string Turn(JsonArray messages, bool first)
    {
        var parts = new List<string>();
        if (first)
        {
            foreach (var m in messages.OfType<JsonObject>().Where(m => IdeaTools.Text(m["role"]) == "user"))
                parts.AddRange(Blocks(m).Where(b => IdeaTools.Text(b["type"]) == "text").Select(b => IdeaTools.Text(b["text"]) ?? ""));
        }
        else
        {
            var lastAssistant = -1;
            for (var i = 0; i < messages.Count; i++) if (IdeaTools.Text(messages[i]?["role"]) == "assistant") lastAssistant = i;
            var uses = messages.OfType<JsonObject>().Where(m => IdeaTools.Text(m["role"]) == "assistant").SelectMany(Blocks)
                .Where(b => IdeaTools.Text(b["type"]) == "tool_use").ToDictionary(b => IdeaTools.Text(b["id"]) ?? "", b => IdeaTools.Text(b["name"]) ?? "");
            for (var i = lastAssistant + 1; i < messages.Count; i++)
            {
                if (messages[i] is not JsonObject m || IdeaTools.Text(m["role"]) != "user") continue;
                foreach (var b in Blocks(m))
                {
                    switch (IdeaTools.Text(b["type"]))
                    {
                        case "text":
                            parts.Add(IdeaTools.Text(b["text"]) ?? "");
                            break;
                        // A server tool's result was already seen by claude in its own turn; only a UI answer is new.
                        case "tool_result" when uses.GetValueOrDefault(IdeaTools.Text(b["tool_use_id"]) ?? "") is { } name && IdeaTools.UiTools.Contains(name):
                            parts.Add($"The human answered {name}: {ResultText(b["content"])}");
                            break;
                    }
                }
            }
        }
        var prompt = string.Join("\n\n", parts.Where(p => p.Length > 0)).Trim();
        return prompt.Length > 0 ? prompt : "Continue.";
    }

    /// <summary>A tool_result's content: a string, or text blocks joined.</summary>
    public static string ResultText(JsonNode? content) => content switch
    {
        JsonArray a => string.Join("\n", a.OfType<JsonObject>().Where(b => IdeaTools.Text(b["type"]) == "text").Select(b => IdeaTools.Text(b["text"]))),
        _ => IdeaTools.Text(content) ?? "",
    };

    private static IEnumerable<JsonObject> Blocks(JsonObject message) => message["content"] switch
    {
        JsonArray a => a.OfType<JsonObject>(),
        JsonValue v => [new JsonObject { ["type"] = "text", ["text"] = IdeaTools.Text(v) }],
        _ => [],
    };

    [GeneratedRegex(@"^---\r?\n.*?\r?\n---\r?\n", RegexOptions.Singleline)] private static partial Regex FrontMatter();
}
