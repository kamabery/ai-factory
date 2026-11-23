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
        // Not yet keeps the idea open until its last backlog spec is decided (contract, "Ideas").
        if (!failed && IdeaClosing.Waiting(session, i.Id) is { Count: > 0 } waiting)
        {
            i.Node["closeWhenDecided"] = true;
            session.Save();
            return CommandResult.Ok($"{i.Id} stays open: {string.Join(", ", waiting)} {(waiting.Count == 1 ? "waits" : "wait")} in backlog");
        }
        var status = failed ? "failed" : "done";
        i.Node["status"] = status;
        i.Node.Remove("closeWhenDecided");
        if (failed) i.Node["reason"] = args.Named("Reason");
        session.Save();
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("idea", i.Id), ("kind", "idea"), ("detail", status)));
        return CommandResult.Ok();
    }
}
