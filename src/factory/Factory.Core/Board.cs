using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>board.json as factory.ps1's Load-Board returns it.</summary>
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
}
