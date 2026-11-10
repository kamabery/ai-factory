using Factory.Core;
using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary><c>status [-Json]</c>: the board, as text or as JSON.</summary>
public sealed class StatusCommand(IBoardReader board, IOptions<CliOptions> options) : ICommand
{
    public string Name => "status";

    public CommandResult Run(CommandArgs args)
    {
        var b = board.Load();
        return args.Has("Json")
            ? CommandResult.Ok(PsJson.Write(b, options.Value.NewLine))
            : new CommandResult(0, StatusReport.Lines(b), []);
    }
}
