using Factory.Cli.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>canary [-Force] [-Full]</c> (contract, "Canary"): see <see cref="ICanaryService.Run"/>.</summary>
public sealed class CanaryCommand(ICanaryService canary) : ICommand
{
    public string Name => "canary";

    public CommandResult Run(CommandArgs args, IBoardSession session) =>
        CommandResult.Ok(canary.Run(session, args.Has("Force"), args.Has("Full")));
}
