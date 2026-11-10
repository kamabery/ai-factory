using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>board.json as factory.ps1's Load-Board returns it, and its jobs and ideas as typed views.</summary>
public static class Board
{
    /// <summary>The board when there is no board.json yet: version, ideas, jobs (contract, "board.json").</summary>
    public static JsonObject Empty() => new() { ["version"] = 2, ["ideas"] = new JsonArray(), ["jobs"] = new JsonArray() };

    /// <summary>The board file, in its own key order, with an empty <c>ideas</c> appended when it has none.</summary>
    public static JsonObject Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject board) throw new FactoryException("board.json is not a JSON object");
        if (!board.ContainsKey("ideas")) board["ideas"] = new JsonArray();
        return board;
    }

    public static IEnumerable<Job> Jobs(this JsonObject board) => Items(board, "jobs").Select(o => new Job(o));

    public static IEnumerable<Idea> Ideas(this JsonObject board) => Items(board, "ideas").Select(o => new Idea(o));

    /// <summary>The job with this id, or a refusal (<c>no job J-NNN</c>).</summary>
    public static Job Job(this JsonObject board, string id) => board.FindJob(id) ?? throw new FactoryException($"no job {id}");

    public static Job? FindJob(this JsonObject board, string? id) => board.Jobs().FirstOrDefault(j => Json.Is(j.Id, id));

    private static IEnumerable<JsonObject> Items(JsonObject board, string key) =>
        board[key] is JsonArray a ? a.OfType<JsonObject>() : [];
}
