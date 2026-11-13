using Factory.Cli.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>canary-destroy</c> (contract, "Canary"): see <see cref="ICanaryService.Teardown"/>.</summary>
public sealed class CanaryDestroyCommand(ICanaryService canary) : ICommand
{
    public string Name => "canary-destroy";

    public CommandResult Run(CommandArgs args, IBoardSession session) => new(0, canary.Teardown(session), []);
}
