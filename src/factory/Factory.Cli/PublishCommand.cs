using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>publish &lt;id&gt;</c>: a ready or blocked job's branch is pushed and its PR opened (once); prints the PR url.</summary>
public sealed class PublishCommand(IJobWork work) : ICommand
{
    public string Name => "publish";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Ready, Stages.Blocked], "publish");
        var (url, err) = work.Publish(session, j);
        if (err is not null) throw new FactoryException($"publish refused: {err}");
        return CommandResult.Ok(url!);
    }
}
