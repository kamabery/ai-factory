using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>The text form of <c>status</c>: open ideas, then jobs that are not discarded.</summary>
public static class StatusReport
{
    public static IReadOnlyList<string> Lines(JsonObject board)
    {
        var ideas = Items(board, "ideas");
        var jobs = Items(board, "jobs");
        // factory.ps1 checks for no jobs and no ideas before it filters out the done ideas.
        if (jobs.Count == 0 && ideas.Count == 0) return ["board is empty"];

        var lines = new List<string>();
        foreach (var i in ideas.Where(i => Text(i, "status") != "done"))
            lines.Add(Format("{0,-6} {1,-10} {2}", Text(i, "id"), "idea:" + Text(i, "status"), Text(i, "text")));
        foreach (var j in jobs.Where(j => Text(j, "stage") != "discarded"))
        {
            var flag = j?["flag"] is JsonObject f ? $" [{Text(f, "reason")}]" : "";
            lines.Add(Format("{0,-6} {1,-10} r{2} {3}{4}", Text(j, "id"), Text(j, "stage"), Text(j, "round"), Text(j, "title"), flag));
        }
        return lines;
    }

    private static List<JsonNode?> Items(JsonObject board, string key) => board[key] is JsonArray a ? [.. a] : [];

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);

    /// <summary>A field as PowerShell interpolates it: a missing or null field is empty.</summary>
    private static string Text(JsonNode? item, string key) => item?[key] switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        var n => n.ToJsonString(),
    };
}
