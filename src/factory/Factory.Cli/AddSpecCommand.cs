using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary><c>add-spec &lt;ideaId&gt; &lt;file&gt;</c>: the spec becomes a backlog feature job of the idea; prints its id.</summary>
public sealed class AddSpecCommand(IFactoryPaths paths, IFileStore files, IOptions<FactoryOptions> options, TimeProvider clock) : ICommand
{
    public string Name => "add-spec";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var idea = session.Board.Idea(args.Arg(1));
        var file = args.Arg(2);
        var text = file.Length == 0 ? null : files.ReadText(Path.GetFullPath(file, options.Value.WorkingDirectory));
        if (text is null) throw new FactoryException($"spec file not found: {file}");
        return CommandResult.Ok(Specs.Add(paths, files, session, idea, PsText.Lines(text), file, "feature", clock));
    }
}
