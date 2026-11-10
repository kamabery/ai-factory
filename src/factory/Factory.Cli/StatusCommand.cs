using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary><c>status [-Json]</c>: the board, as text or as JSON.</summary>
public sealed class StatusCommand(IOptions<CliOptions> options) : ICommand
{
    public string Name => "status";

    public CommandResult Run(CommandArgs args, IBoardSession session) =>
        args.Has("Json")
            ? CommandResult.Ok(PsJson.Write(session.Board, options.Value.NewLine))
            : new CommandResult(0, StatusReport.Lines(session.Board), []);
}
