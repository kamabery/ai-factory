using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>idea-done &lt;ideaId&gt; [-Failed -Reason &lt;text&gt;]</c>: the idea session ends, specced or not.</summary>
public sealed class IdeaDoneCommand(TimeProvider clock) : ICommand
{
    public string Name => "idea-done";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var i = session.Board.Idea(args.Arg(1));
        var failed = args.Has("Failed");
        var status = failed ? "failed" : "done";
        i.Node["status"] = status;
        if (failed) i.Node["reason"] = args.Named("Reason");
        session.Save();
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("idea", i.Id), ("kind", "idea"), ("detail", status)));
        return CommandResult.Ok();
    }
}
