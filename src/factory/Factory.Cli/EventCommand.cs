using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>event &lt;json&gt;</c>, or <c>event -Kind k [-Job -Idea -Agent -Detail]</c>: appends one event (hook-event.ps1, agents).
/// A job's event gets the job's round. In azure, an agent-stop copies the subagent's transcript into the job.
/// </summary>
public sealed class EventCommand(IFactoryPaths paths, IFileStore files, IStateSettings settings, TimeProvider clock) : ICommand
{
    public string Name => "event";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        JsonObject e;
        if (args.Arg(1).Length > 0)
        {
            try { e = JsonNode.Parse(args.Arg(1)) as JsonObject ?? throw new FactoryException("an event must be a JSON object"); }
            catch (JsonException x) { throw new FactoryException($"the event is not JSON: {x.Message}"); }
        }
        else
        {
            e = [];
            if (args.Named("Job").Length > 0) e["job"] = args.Named("Job");
            if (args.Named("Idea").Length > 0) e["idea"] = args.Named("Idea");
            e["kind"] = args.Get("Kind");
            if (args.Named("Agent").Length > 0) e["agent"] = args.Named("Agent");
            if (args.Named("Detail").Length > 0) e["detail"] = args.Named("Detail");
        }
        Events.RequireKind(PsJsonReader.Str(e["kind"]));
        if (PsJsonReader.Truthy(e["job"]) && !e.ContainsKey("round") && session.Board.FindJob(PsJsonReader.Text(e["job"])) is { } j)
            e["round"] = j.Node["round"]?.DeepClone();
        var line = Events.Create(clock.GetUtcNow());
        foreach (var (k, v) in e) line[k] = v?.DeepClone();
        session.AddEvent(line);
        if (settings.SharesFiles) CopyTranscript(e);
        return CommandResult.Ok();
    }

    /// <summary>A subagent's transcript lives under ~/.claude on the machine that ran it: its agent-stop copies it into the job's folder, so the push takes it to the container.</summary>
    private void CopyTranscript(JsonObject e)
    {
        if (!PsJsonReader.Is(PsJsonReader.Str(e["kind"]), "agent-stop") || !PsJsonReader.Truthy(e["job"])) return;
        var job = PsJsonReader.Text(e["job"]);
        var agent = PsJsonReader.Str(e["agent"]);
        JsonObject? start = null;
        foreach (var l in PsText.Lines(files.ReadText(paths.EventsPath)))
        {
            if (!l.Contains("\"kind\":\"agent-start\"", StringComparison.OrdinalIgnoreCase)) continue;
            JsonObject? s;
            try { s = JsonNode.Parse(l) as JsonObject; } catch (JsonException) { continue; }
            if (s is not null && PsJsonReader.Is(PsJsonReader.Str(s["job"]), job) && PsJsonReader.Is(PsJsonReader.Str(s["agent"]), agent)
                && PsJsonReader.Truthy(s["transcript"])) start = s;
        }
        if (start is null || PsJsonReader.Str(start["transcript"]) is not { } transcript || files.Stat(transcript) is null) return;
        files.CopyFile(transcript, Path.Combine(paths.JobDir(job), "transcripts", $"agent-{PsJsonReader.Text(start["agentId"])}.jsonl"));
    }
}
