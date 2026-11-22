using System.Text.Json.Nodes;

namespace Factory.Server;

/// <summary>Contract, "Idea sessions (dashboard)", "The phase": brief, scaffold, specs or build, from session.json and the board.</summary>
public static class SessionPhase
{
    public static JsonObject Of(JsonObject session, IEnumerable<JsonObject> jobs)
    {
        var board = jobs.ToList();
        JsonObject? Job(string? id) => id is null ? null : board.FirstOrDefault(j => BoardFile.Str(j, "id") == id);

        // A New repo session (spec 22): grill → scaffold → review → merged; it writes no specs.
        if (BoardFile.Str(session, "kind") == "repo")
        {
            if (BoardFile.Str(session, "idea") is null) return new() { ["name"] = "grill" };
            if (Job(BoardFile.Str(session, "bootstrap")) is not { } j || BoardFile.Str(j, "stage") == "done") return new() { ["name"] = "merged" };
            return new()
            {
                ["name"] = BoardFile.Str(j, "stage") == "ready" ? "review" : "scaffold",
                ["job"] = BoardFile.Str(j, "id"), ["stage"] = BoardFile.Str(j, "stage"), ["pr"] = j["pr"]?.DeepClone(),
            };
        }
        if (BoardFile.Str(session, "idea") is null) return new() { ["name"] = "brief" };
        // A bootstrap purged off the board was done or discarded: either way the scaffold is behind the session.
        if (Job(BoardFile.Str(session, "bootstrap")) is { } boot && BoardFile.Str(boot, "stage") != "done")
            return new() { ["name"] = "scaffold", ["job"] = BoardFile.Str(boot, "id"), ["stage"] = BoardFile.Str(boot, "stage"), ["pr"] = boot["pr"]?.DeepClone() };

        var specs = (session["specs"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (specs.Count == 0 || specs.Any(s => string.IsNullOrEmpty(BoardFile.Str(s, "decision")))) return new() { ["name"] = "specs" };
        var built = specs.Select(s => Job(BoardFile.Str(s, "job"))).OfType<JsonObject>().ToList();
        if (built.Count == 0) return new() { ["name"] = "specs" };
        return new()
        {
            ["name"] = "build",
            ["jobs"] = new JsonArray([.. built.Select(j => (JsonNode)new JsonObject
            {
                ["id"] = BoardFile.Str(j, "id"), ["title"] = BoardFile.Str(j, "title"), ["stage"] = BoardFile.Str(j, "stage"),
                ["pr"] = j["pr"]?.DeepClone(), ["deploy"] = j["deploy"]?.DeepClone(),
            })]),
        };
    }
}
