using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary><c>edit-spec &lt;id&gt; -File &lt;path&gt;</c>: replaces a backlog job's spec, and its target with the spec's.</summary>
public sealed class EditSpecCommand(IFactoryPaths paths, IFileStore files, IOptions<FactoryOptions> options, TimeProvider clock) : ICommand
{
    public string Name => "edit-spec";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Backlog], "edit-spec");
        var file = args.Named("File");
        var full = file.Length == 0 ? "" : Path.GetFullPath(file, options.Value.WorkingDirectory);
        if (file.Length == 0 || files.Stat(full) is null) throw new FactoryException($"spec file not found: {file}");
        files.CopyFile(full, Path.Combine(paths.JobDir(j.Id), "spec.md"));
        j.Node["target"] = SpecText.Meta(PsText.Lines(files.ReadText(full))).Repo;
        var now = clock.GetUtcNow();
        j.Node["updatedAt"] = Iso.Format(now);
        session.Save();
        session.AddEvent(Events.Create(now, ("job", j.Id), ("kind", "note"), ("detail", "spec edited")));
        return CommandResult.Ok();
    }
}
