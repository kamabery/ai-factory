using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>provenance &lt;id&gt;</c>: build check 3's template-sourced paths for this round, one per line; a note goes to stderr.</summary>
public sealed class ProvenanceCommand(ITemplateProvenance provenance) : ICommand
{
    public string Name => "provenance";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var p = provenance.For(session.Board, session.Board.Job(args.Arg(1)));
        return new CommandResult(0, p.Paths ?? [], p.Note is null ? [] : [$"factory: note: {p.Note}"]);
    }
}
