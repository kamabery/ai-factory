using System.Text.Json.Nodes;
using Factory.Store.Abstractions;

namespace Factory.UnitTests;

/// <summary>An open board in memory: what a command saved and the events it added, in order.</summary>
public sealed class FakeSession(JsonObject board) : IBoardSession
{
    public JsonObject Board { get; private set; } = board;
    public int Saves { get; private set; }

    /// <summary>How many times a command released the lock.</summary>
    public int Unlocks { get; private set; }

    /// <summary>What another process does to the board while the lock is released; the board is then read again.</summary>
    public Action<JsonObject>? WhileUnlocked { get; set; }

    public void Unlocked(Action work)
    {
        Unlocks++;
        work();
        var next = JsonNode.Parse(Board.ToJsonString())!.AsObject();
        WhileUnlocked?.Invoke(next);
        Board = next;
    }
    public List<JsonObject> Events { get; } = [];

    /// <summary>The board as last saved, or null before any save.</summary>
    public string? Saved { get; private set; }

    public void Save() { Saves++; Saved = Board.ToJsonString(); }

    public void AddEvent(JsonObject e) => Events.Add(e);

    /// <summary>What a command shared at once, in order; <see cref="OnShare"/> sees each as it happens.</summary>
    public List<string> Shared { get; } = [];

    public Action<string>? OnShare { get; set; }

    public void Share(string path) { Shared.Add(path); OnShare?.Invoke(path); }

    public IReadOnlyList<string> Close() => [];

    public void Dispose() { }

    public static FakeSession With(params JsonObject[] jobs) =>
        new(new JsonObject { ["version"] = 2, ["ideas"] = new JsonArray(), ["jobs"] = new JsonArray([.. jobs]) });

    public JsonObject Job(string id) => Board["jobs"]!.AsArray().OfType<JsonObject>().Single(j => (string?)j["id"] == id);

    public IEnumerable<string?> Kinds => Events.Select(e => (string?)e["kind"]);
}
