using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>gate &lt;id&gt;</c>: the deterministic gate (contract, "Gate and merge"). Green lands in ready; red lands in ready flagged
/// gate-failed and exits 1; a gate that cannot run lands in ready flagged escalated.
/// </summary>
public sealed class GateJobCommand(IJobWork work, TimeProvider clock) : ICommand
{
    public string Name => "gate";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var id = args.Arg(1);
        Transitions.RequireStage(session.Board.Job(id), [Stages.Gate], "gate");
        var g = work.Gate(session, id);
        var j = session.Board.Job(id);
        if (g is not null)
        {
            BoardMoves.Move(session, j, Stages.Ready, "gate", clock, "escalated", g);
            throw new FactoryException(g);
        }
        if (work.GateGreen(j))
        {
            BoardMoves.Move(session, j, Stages.Ready, "gate", clock);
            return CommandResult.Ok();
        }
        BoardMoves.Move(session, j, Stages.Ready, "gate", clock, "gate-failed", "a gate command exited non-zero; see gate.log");
        return new CommandResult(1, [], []);
    }
}
