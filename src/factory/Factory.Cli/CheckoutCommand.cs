using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>checkout &lt;id&gt;</c>: clones or reuses the job's work dir on factory/&lt;id&gt;, fetches, points <c>factory/base</c> at the
/// base branch as it is now, records a build round's start, and prints the work dir.
/// </summary>
public sealed class CheckoutCommand(IFactoryPaths paths, IGitClient git, IJobWork work) : ICommand
{
    public string Name => "checkout";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var id = args.Arg(1);
        var j = session.Board.Job(id);
        var wt = paths.WorkDir(j.Id);
        string? err = null;
        // The clone and fetch are a long step: the board is released around them, so a dashboard move doesn't wait
        // them out (CI: ~12 s) or, on a big repo, time out on the lock.
        session.Unlocked(() =>
        {
            err = work.EnsureClone(session.Board, j.Id);
            // factory/base is the job's base branch as it is now, for the builder to merge in: agents have no GitHub credential to
            // fetch with, and a branch cut before another job merged is stale (J-022).
            if (err is null && work.Fetch(wt) is { Code: not 0 } f) err = $"checkout: fetch failed: {f.Out}";
            if (err is null && git.Run(wt, "update-ref", "refs/remotes/factory/base", $"refs/remotes/origin/{MergeRules.BaseBranch(session.Board, j)}") is { Code: not 0 } u)
                err = $"checkout: cannot set factory/base: {u.Out}";
        });
        if (err is not null) throw new FactoryException(err);
        j = session.Board.Job(id);
        // The round's start, for the provenance check: the first checkout of a build round only.
        if (PsJsonReader.Is(j.Stage, Stages.Build) && !PsJsonReader.Truthy(j.Node["roundStart"]))
        {
            j.Node["roundStart"] = git.Run(wt, "rev-parse", "HEAD").Out;
            session.Save();
        }
        return CommandResult.Ok(wt);
    }
}
