using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>discard &lt;id&gt;</c>: the human throws a backlog, ready or blocked job away. Nothing may be stacked on it; its PR is
/// closed, its branch and work dir deleted, and a preview still up is destroyed.
/// </summary>
public sealed class DiscardCommand(IJobWork work, TimeProvider clock) : ICommand
{
    public string Name => "discard";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Backlog, Stages.Ready, Stages.Blocked], "discard");
        MergeRules.RequireNothingStacked(session.Board, j);
        BoardMoves.Move(session, j, Stages.Discarded, "human", clock);
        work.RemoveJobWork(session, j);
        work.StartDestroy(j);
        return CommandResult.Ok();
    }
}
