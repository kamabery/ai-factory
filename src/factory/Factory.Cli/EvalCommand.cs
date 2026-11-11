using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary>
/// <c>eval &lt;id&gt; -File &lt;eval.md&gt;</c>: records a finished job's evaluation (contract, "Evaluations"): the markdown as
/// <c>evals/&lt;id&gt;.md</c>, and a snapshot <c>evals/&lt;id&gt;.json</c> of the job, its models' cost and tokens from the run logs,
/// and each agent's time and the gate's from events.jsonl. A human rating already given is kept.
/// </summary>
public sealed partial class EvalCommand(IFactoryPaths paths, IFileStore files, IOptions<FactoryOptions> options, TimeProvider clock) : ICommand
{
    public string Name => "eval";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Ready, Stages.Done, Stages.Discarded], "eval");
        var file = args.Named("File");
        var text = file.Length == 0 ? null : files.ReadText(Path.GetFullPath(file, options.Value.WorkingDirectory));
        if (text is null) throw new FactoryException($"eval file not found: {file}");
        var rating = EvalRating.Of(SpecText.FirstLine(text)) ?? throw new FactoryException($"no 'Rating: good|mixed|poor' first line in {file}", 2);
        var n = j.Node;
        var flag = n["flag"] as JsonObject;
        var snap = new JsonObject
        {
            ["job"] = j.Id, ["idea"] = n["idea"]?.DeepClone(), ["title"] = n["title"]?.DeepClone(), ["kind"] = n["kind"]?.DeepClone() ?? "feature",
            ["target"] = n["target"]?.DeepClone(), ["rating"] = rating, ["stage"] = n["stage"]?.DeepClone(), ["round"] = n["round"]?.DeepClone(),
            ["flag"] = flag is null ? null : flag["reason"]?.DeepClone(), ["reviews"] = n["reviews"]?.DeepClone(), ["decision"] = n["decision"]?.DeepClone(),
            ["gate"] = GateVerdict(n["gate"] as JsonObject), ["merged"] = n["merged"] is JsonObject m ? m["sha"]?.DeepClone() : null,
            ["costUsd"] = n["costUsd"]?.DeepClone(), ["durationMs"] = n["durationMs"]?.DeepClone(), ["evaluatedAt"] = Iso.Format(clock.GetUtcNow()),
            ["models"] = Models(j.Id), ["agents"] = null, ["gateMs"] = null,
        };
        var (agents, gateMs) = Timings(j.Id);
        snap["agents"] = agents;
        snap["gateMs"] = gateMs;
        var dir = Path.Combine(paths.Home, "evals");
        var json = Path.Combine(dir, $"{j.Id}.json");
        if (Read(json)?["human"] is { } human && PsJsonReader.Truthy(human)) snap["human"] = human.DeepClone();
        files.EnsureDirectory(dir);
        files.CopyFile(Path.GetFullPath(file, options.Value.WorkingDirectory), Path.Combine(dir, $"{j.Id}.md"));
        files.WriteAtomic(json, Encoding.UTF8.GetBytes(PsJson.Write(snap, Environment.NewLine)));
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "eval"), ("detail", rating)));
        return CommandResult.Ok();
    }

    /// <summary>null with no gate, red when a command exited non-zero, else green.</summary>
    private static string? GateVerdict(JsonObject? gate)
    {
        if (gate is null) return null;
        return gate.Any(kv => !PsJsonReader.In(kv.Key, "sha", "at") && PsJsonReader.Text(kv.Value) != "0") ? "red" : "green";
    }

    /// <summary>
    /// Each model's cost and output tokens. A run's last result line carries the session's usage so far (a run woken
    /// again by a background task logs several), so that one is the run's total, as the runner records it.
    /// </summary>
    private JsonObject Models(string id)
    {
        var dir = paths.JobDir(id);
        var acc = new List<(string Model, double Cost, long Tokens)>();
        var runs = files.Files(dir).Where(f => Path.GetDirectoryName(f.Path) == Path.GetFullPath(dir) && RunLog().IsMatch(Path.GetFileName(f.Path)))
            .OrderBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
        {
            var last = PsText.Lines(files.ReadText(run.Path)).LastOrDefault(l => ResultLine().IsMatch(l));
            if (last is null || Parse(last) is not { } r || r["modelUsage"] is not JsonObject usage) continue;
            foreach (var (model, u) in usage)
            {
                var i = acc.FindIndex(a => a.Model == model);
                if (i < 0) { acc.Add((model, 0.0, 0)); i = acc.Count - 1; }
                var cost = u?["costUSD"] is JsonValue c ? PsJsonReader.Number(c) : 0;
                var tokens = u?["outputTokens"] is JsonValue t ? (long)PsJsonReader.Number(t) : 0;
                acc[i] = (model, Math.Round(acc[i].Cost + cost, 4), acc[i].Tokens + tokens);
            }
        }
        var models = new JsonObject();
        foreach (var (model, cost, tokens) in acc) models[model] = new JsonObject { ["costUsd"] = PsNumber.Double(cost), ["outputTokens"] = tokens };
        return models;
    }

    /// <summary>Each agent's time per round (agent-start to agent-stop), and the gate's (the move into gate to its gate event).</summary>
    private (JsonArray Agents, JsonNode? GateMs) Timings(string id)
    {
        var open = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var agents = new List<(string Key, JsonObject Row)>();
        long? gateMs = null;
        DateTimeOffset? gateAt = null;
        foreach (var line in PsText.Lines(files.ReadText(paths.EventsPath)))
        {
            if (Parse(line) is not { } e || !PsJsonReader.Is(PsJsonReader.Str(e["job"]), id)) continue;
            var key = $"{PsJsonReader.Text(e["agent"])}|{PsJsonReader.Text(e["round"])}";
            var ts = DateTimeOffset.Parse(PsJsonReader.Text(e["ts"]), CultureInfo.InvariantCulture);
            switch (PsJsonReader.Text(e["kind"]).ToLowerInvariant())
            {
                case "agent-start":
                    open[key] = ts;
                    break;
                case "agent-stop" when open.Remove(key, out var start):
                    var row = agents.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)).Row;
                    if (row is null)
                    {
                        row = new JsonObject { ["agent"] = e["agent"]?.DeepClone(), ["round"] = e["round"]?.DeepClone(), ["ms"] = 0L };
                        agents.Add((key, row));
                    }
                    row["ms"] = (long)row["ms"]! + Ms(start, ts);
                    break;
                case "stage" when PsJsonReader.Is(PsJsonReader.Str(e["to"]), Stages.Gate):
                    gateAt = ts;
                    break;
                case "gate" when gateAt is { } at:
                    gateMs = (gateMs ?? 0) + Ms(at, ts);
                    gateAt = null;
                    break;
            }
        }
        return (new JsonArray([.. agents.Select(a => (JsonNode)a.Row)]), gateMs is { } g ? JsonValue.Create(g) : null);
    }

    private static long Ms(DateTimeOffset from, DateTimeOffset to) => (long)Math.Round((to - from).TotalMilliseconds);

    private JsonObject? Read(string path)
    {
        try { return files.ReadText(path) is { } t ? JsonNode.Parse(t) as JsonObject : null; }
        catch (JsonException) { return null; }
    }

    private static JsonObject? Parse(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"^run-.*\.jsonl$", RegexOptions.IgnoreCase)] private static partial Regex RunLog();
    [GeneratedRegex(@"""type""\s*:\s*""result""")] private static partial Regex ResultLine();
}
