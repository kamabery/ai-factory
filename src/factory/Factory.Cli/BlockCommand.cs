using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>block &lt;id&gt; -Detail &lt;line&gt;</c>: the devops agent judged the environment or the factory at fault, which no agent can
/// fix, so the job waits flagged environment for a human (contract, "The deploy loop").
/// </summary>
public sealed class BlockCommand(IDotEnv dotEnv, TimeProvider clock) : ICommand
{
    public string Name => "block";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        DeployRules.RequireFailedDeploy(j, "block");
        var detail = args.Named("Detail");
        if (detail.Length == 0) throw new FactoryException("usage: block <id> -Detail <one line>");
        BoardMoves.Move(session, j, Transitions.EscalationStage(j, dotEnv.Get("HCP_ORG") is not null), "orchestrator", clock, "environment", detail);
        return CommandResult.Ok(j.Stage);
    }
}
