using System.Text;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>rework &lt;id&gt; &lt;note&gt;</c>: the human sends a ready or blocked job back to the queue for another round, with a
/// note the builder reads (rework-&lt;n&gt;.md). <c>Scope +:</c> lines widen the spec's Scope: In.
/// </summary>
public sealed class ReworkCommand(IFactoryPaths paths, IFileStore files, TimeProvider clock) : ICommand
{
    public string Name => "rework";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Ready, Stages.Blocked], "rework");
        var note = args.Arg(2);
        if (note.Length == 0) throw new FactoryException("usage: rework <id> <note>");
        var now = clock.GetUtcNow();
        var events = new List<JsonObject>();
        var specPath = Path.Combine(paths.JobDir(j.Id), "spec.md");
        var widen = Rework.Widening(note);
        IReadOnlyList<string> lines = [];
        ScopeIn? scope = null;
        if (widen.Count > 0)
        {
            lines = PsText.Lines(files.ReadText(specPath));
            scope = SpecText.ScopeIn(lines);
            if (scope.Globs.Count == 0)
                throw new FactoryException("rework refused: Scope + widens the spec's Scope: In globs, and this spec has none (it is not scope-checked)");
        }
        var round = j.Round + 1;
        j.Node["round"] = round;
        if (scope is not null)
        {
            Write(specPath, PsText.Content(Rework.Widen(lines, scope.At, widen, round), Environment.NewLine));
            events.Add(Events.Create(now, ("job", j.Id), ("kind", "note"), ("detail", $"scope widened (rework {round}): {string.Join(", ", widen)}")));
        }
        Write(Path.Combine(paths.JobDir(j.Id), $"rework-{round}.md"), PsText.Content([note], Environment.NewLine));
        Transitions.ResetReviews(j);
        j.Node["decision"] = null;
        j.Node["approvedAt"] = Iso.Format(now);
        // The human has now decided against the new main.
        if (PsJsonReader.Truthy(j.Node["baseMoved"])) j.Node["baseMoved"] = null;
        if (j.Node["deploy"] is JsonObject d && PsJsonReader.Truthy(d)) d["attempts"] = 0;
        j.Node["deployFixes"] = 0;
        // The preview stays: the next deploy applies over it (a destroy here raced that apply, J-022).
        events.Add(Transitions.Move(j, Stages.Approved, "human", null, null, null, now));
        session.Save();
        foreach (var e in events) session.AddEvent(e);
        return CommandResult.Ok();
    }

    private void Write(string path, string text) => files.WriteAtomic(path, new UTF8Encoding(false).GetBytes(text));
}
