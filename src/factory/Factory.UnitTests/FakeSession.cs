using System.Text.Json.Nodes;
using Factory.Store.Abstractions;

namespace Factory.UnitTests;

/// <summary>An open board in memory: what a command saved and the events it added, in order.</summary>
public sealed class FakeSession(JsonObject board) : IBoardSession
{
    public JsonObject Board { get; } = board;
    public int Saves { get; private set; }
    public List<JsonObject> Events { get; } = [];

    /// <summary>The board as last saved, or null before any save.</summary>
    public string? Saved { get; private set; }

    public void Save() { Saves++; Saved = Board.ToJsonString(); }

    public void AddEvent(JsonObject e) => Events.Add(e);

    public IReadOnlyList<string> Close() => [];

    public void Dispose() { }

    public static FakeSession With(params JsonObject[] jobs) =>
        new(new JsonObject { ["version"] = 2, ["ideas"] = new JsonArray(), ["jobs"] = new JsonArray([.. jobs]) });

    public JsonObject Job(string id) => Board["jobs"]!.AsArray().OfType<JsonObject>().Single(j => (string?)j["id"] == id);

    public IEnumerable<string?> Kinds => Events.Select(e => (string?)e["kind"]);
}
