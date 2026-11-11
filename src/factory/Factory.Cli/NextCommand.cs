using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>next [-NeedsCanary]</c>: the job the runner may take, skipping deployable jobs until a canary has passed; with
/// -NeedsCanary, <c>canary</c> when one is held back only for want of a canary. Prints nothing for nothing.
/// </summary>
public sealed class NextCommand(IDotEnv dotEnv, ICanary canary) : ICommand
{
    public string Name => "next";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var next = Queue.Next(session.Board, dotEnv.Get("HCP_ORG") is not null, canary.State, args.Has("NeedsCanary"));
        return next is null ? CommandResult.Ok() : CommandResult.Ok(next);
    }
}
