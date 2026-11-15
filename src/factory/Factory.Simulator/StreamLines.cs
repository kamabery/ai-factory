using System.Text.Json.Nodes;

namespace Factory.Simulator;

/// <summary>Fake stream-json lines, shaped like <c>claude -p --output-format stream-json --verbose</c>, for the Output tab.</summary>
public static class StreamLines
{
    public static string Init() => new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "sim" }.ToJsonString();

    public static string Say(string text, string? parent = null) => Assistant(new JsonObject { ["type"] = "text", ["text"] = text }, parent);

    public static string Think(string? parent = null) => Assistant(new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sim" }, parent);

    public static string Tool(string id, string name, JsonObject input) =>
        new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }) } }.ToJsonString();

    public static string Result(string id, string text) =>
        new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = text }) } }.ToJsonString();

    public static string Done(double usd, int ms) => new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["total_cost_usd"] = usd, ["duration_ms"] = ms, ["result"] = "done",
        ["modelUsage"] = new JsonObject
        {
            ["claude-opus-5-5"] = new JsonObject { ["costUSD"] = 0.38, ["outputTokens"] = 4200 },
            ["claude-sonnet-5-5"] = new JsonObject { ["costUSD"] = Math.Round(usd - 0.38, 2), ["outputTokens"] = 900 },
        },
    }.ToJsonString();

    private static string Assistant(JsonObject block, string? parent) =>
        new JsonObject { ["type"] = "assistant", ["parent_tool_use_id"] = parent, ["message"] = new JsonObject { ["content"] = new JsonArray(block) } }.ToJsonString();
}
