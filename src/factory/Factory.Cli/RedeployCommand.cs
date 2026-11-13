using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>redeploy &lt;id&gt;</c>: once the human fixed what stopped a job's deploy, it goes back to the queue to resume at deploy
/// rather than build again, with a fresh attempt budget: the attempts spent on the cause don't count against the change.
/// </summary>
public sealed class RedeployCommand(TimeProvider clock) : ICommand
{
    public string Name => "redeploy";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        DeployRules.RequireRedeployable(j);
        j.Node["resume"] = "deploy";
        if (j.Node["deploy"] is JsonObject d) d["attempts"] = 0;
        j.Node["approvedAt"] = Iso.Format(clock.GetUtcNow());
        BoardMoves.Move(session, j, Stages.Approved, "human", clock);
        return CommandResult.Ok();
    }
}
