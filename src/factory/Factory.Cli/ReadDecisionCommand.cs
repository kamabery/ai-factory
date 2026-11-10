using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>read-decision &lt;id&gt;</c>: the approver's decision.md moves the job to gate, or escalates it; prints its stage.</summary>
public sealed class ReadDecisionCommand(IFactoryPaths paths, IFileStore files, TimeProvider clock) : ICommand
{
    public string Name => "read-decision";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Approve], "read-decision");
        var e = Verdicts.ReadDecision(j, files.ReadText(Path.Combine(paths.JobDir(j.Id), "decision.md")), clock.GetUtcNow());
        session.Save();
        session.AddEvent(e);
        return CommandResult.Ok(j.Stage);
    }
}
