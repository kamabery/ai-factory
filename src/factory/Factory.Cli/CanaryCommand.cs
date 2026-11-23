using Factory.Cli.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>canary [-Force] [-Full] [-Retry] [-Due]</c> (contract, "Canary"): see <see cref="ICanaryService"/>.</summary>
public sealed class CanaryCommand(ICanaryService canary) : ICommand
{
    public string Name => "canary";

    public CommandResult Run(CommandArgs args, IBoardSession session) =>
        CommandResult.Ok(args.Has("Due") ? Core.PsJson.WriteCompact(canary.Due())
            : args.Has("Retry") ? canary.Retry() : canary.Run(session, args.Has("Force"), args.Has("Full")));
}
