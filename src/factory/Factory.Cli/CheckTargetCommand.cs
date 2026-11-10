using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>check-target &lt;owner/name&gt;</c>: prints <c>new</c> or <c>existing</c> from the GitHub API.</summary>
public sealed class CheckTargetCommand(IGitHub github) : ICommand
{
    public string Name => "check-target";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var t = args.Arg(1);
        if (SpecText.TargetError(t) is { } err) throw new FactoryException($"check-target refused: {err}");
        return CommandResult.Ok(Targets.IsNew(github, t, "check-target") ? "new" : "existing");
    }
}
