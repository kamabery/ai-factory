using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Data.Abstractions;

namespace Factory.Data;

/// <summary>
/// The board as rows and back (contract, "State backends", Database), with no I/O: a skeleton of the board's own keys,
/// one JSON object per idea and job in board order, and what a save must write given what the command read.
/// </summary>
public static class BoardRows
{
    private static readonly string[] Lists = ["ideas", "jobs"];

    /// <summary>board.json without its ideas and jobs, compact: those keys stay, in their places, as empty arrays.</summary>
    public static string Skeleton(JsonObject board)
    {
        var s = new JsonObject();
        foreach (var (k, v) in board) s[k] = Lists.Contains(k) ? new JsonArray() : v?.DeepClone();
        return PsJson.WriteCompact(s);
    }

    /// <summary>The board from its skeleton and its ideas' and jobs' JSON, each in board order.</summary>
    public static JsonObject Assemble(string skeleton, IEnumerable<string> ideas, IEnumerable<string> jobs)
    {
        var board = (JsonObject)JsonNode.Parse(skeleton)!;
        Fill(board, "ideas", ideas);
        Fill(board, "jobs", jobs);
        return board;
    }

    private static void Fill(JsonObject board, string key, IEnumerable<string> rows)
    {
        var items = rows.Select(r => JsonNode.Parse(r)).ToArray();
        if (board.ContainsKey(key) || items.Length > 0) board[key] = new JsonArray(items);
    }

    /// <summary>What saving <paramref name="board"/> writes, given what was read. Refuses a board whose ideas or jobs lack or repeat an id.</summary>
    public static BoardPlan Plan(BoardRead read, JsonObject board)
    {
        var skeleton = Skeleton(board);
        var rows = new List<RowChange>();
        rows.AddRange(Changes(RowKind.Idea, Items(board, "ideas", "idea"), read.Ideas));
        rows.AddRange(Changes(RowKind.Job, Items(board, "jobs", "job"), read.Jobs));
        return new BoardPlan(skeleton, !string.Equals(skeleton, read.Skeleton, StringComparison.Ordinal), rows);
    }

    private static List<(string Id, JsonObject Node)> Items(JsonObject board, string key, string noun)
    {
        var list = new List<(string, JsonObject)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in board[key] is JsonArray a ? a.OfType<JsonObject>() : [])
        {
            var id = PsJsonReader.Str(o["id"]);
            if (string.IsNullOrEmpty(id)) throw new FactoryException($"the board has a{(noun == "idea" ? "n" : "")} {noun} with no id");
            if (!seen.Add(id)) throw new FactoryException($"the board has two {noun}s {id}");
            list.Add((id, o));
        }
        return list;
    }

    private static IEnumerable<RowChange> Changes(RowKind kind, List<(string Id, JsonObject Node)> items, IReadOnlyDictionary<string, RowRead> read)
    {
        // Rows keep the place they were read at, and new ones go after the last, so a purge or an insert rewrites no
        // other row; only a board whose order changed is renumbered.
        var next = read.Count == 0 ? 0 : read.Values.Max(r => r.Ordinal) + 1;
        var places = items.Select(i => read.TryGetValue(i.Id, out var r) ? r.Ordinal : next++).ToList();
        var ordered = places.Zip(places.Skip(1)).All(p => p.First < p.Second);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < items.Count; n++)
        {
            var (id, node) = items[n];
            ids.Add(id);
            var json = PsJson.WriteCompact(node);
            var place = ordered ? places[n] : n;
            if (!read.TryGetValue(id, out var was)) yield return new RowChange(kind, ChangeKind.Insert, id, json, place, 0, node);
            else if (!string.Equals(was.Json, json, StringComparison.Ordinal) || was.Ordinal != place)
                yield return new RowChange(kind, ChangeKind.Update, id, json, place, was.Version, node);
        }
        foreach (var (id, was) in read)
            if (!ids.Contains(id)) yield return new RowChange(kind, ChangeKind.Delete, id, null, was.Ordinal, was.Version, null);
    }

    /// <summary>An events line's ts, job, idea and kind.</summary>
    public static EventColumns Columns(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is JsonObject o)
                return new EventColumns(PsJsonReader.Str(o["ts"]), PsJsonReader.Str(o["job"]), PsJsonReader.Str(o["idea"]), PsJsonReader.Str(o["kind"]));
        }
        catch (JsonException) { }
        return new EventColumns(null, null, null, null);
    }
}
