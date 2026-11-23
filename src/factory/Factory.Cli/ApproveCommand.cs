using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>approve &lt;id&gt;</c>: the human approves a backlog spec that names its target and gate.</summary>
public sealed class ApproveCommand(IFactoryPaths paths, IFileStore files, TimeProvider clock) : ICommand
{
    public string Name => "approve";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Backlog], "approve");
        var text = files.ReadText(Path.Combine(paths.JobDir(j.Id), "spec.md")) ?? throw new FactoryException($"{j.Id} has no spec.md");
        var meta = SpecText.Meta(PsText.Lines(text));
        if (string.IsNullOrEmpty(meta.Repo)) throw new FactoryException("approve refused: the spec has no '## Target' with '- Repo: <owner/name>'");
        if (SpecText.TargetError(meta.Repo) is { } err) throw new FactoryException($"approve refused: {err}");
        if (meta.Gate.Count == 0) throw new FactoryException("approve refused: the spec's '## Gate' has no '- <name>: `<command>`' lines");
        var shellLines = meta.Gate.Select(g => (g.Name, Problem: GateLine.Problem(g.Cmd))).Where(g => g.Problem is not null)
            .Select(g => $"{g.Name}: {g.Problem!.Reason}, so {g.Problem.Hint}").ToList();
        if (shellLines.Count > 0)
            throw new FactoryException($"approve refused: {j.Id}'s gate runs with no shell (contract, \"Gate\"): {string.Join("; ", shellLines)}");
        if (SpecText.AmbiguousScope(PsText.Lines(text)) is [var vague, ..] ) throw new FactoryException($"approve refused: {vague}");
        var now = clock.GetUtcNow();
        j.Node["target"] = meta.Repo;
        j.Node["approvedAt"] = Iso.Format(now);
        var e = Transitions.Move(j, Stages.Approved, "human", null, null, null, now);
        session.Save();
        session.AddEvent(e);
        IdeaClosing.AfterDecided(session, j, clock);
        return CommandResult.Ok();
    }
}
