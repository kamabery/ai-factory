using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>resume &lt;id&gt;</c>: a job a usage limit stopped goes back to the queue, to continue its own session in the stage it
/// stopped in (contract, "Usage limits"); nothing is re-run.
/// </summary>
public sealed class ResumeCommand(TimeProvider clock) : ICommand
{
    public string Name => "resume";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Ready, Stages.Blocked], "resume");
        if (!PsJsonReader.Is(j.Flag?.Reason, "usage-limit"))
            throw new FactoryException($"resume refused: {j.Id} is not flagged usage-limit (rework, redeploy or discard it)");
        var flag = (JsonObject)j.Node["flag"]!;
        j.Node["resume"] = flag["stage"]?.DeepClone();
        j.Node["resumeSession"] = flag["session"]?.DeepClone();
        j.Node["approvedAt"] = Iso.Format(clock.GetUtcNow());
        BoardMoves.Move(session, j, Stages.Approved, "human", clock);
        return CommandResult.Ok();
    }
}
