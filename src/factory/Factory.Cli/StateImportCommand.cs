using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Data.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>state-import [-Azure]</c>: the one-time move of FACTORY_HOME's board.json and events.jsonl, or with -Azure the
/// container's, into an empty sqlite or sql database (contract, "State backends", Database). Its board is not refreshed
/// first: the database has none yet.
/// </summary>
public sealed class StateImportCommand(
    IStateSettings settings, IStateSync sync, IFactoryPaths paths, IFileStore files, IBoardRows rows, IStateExport export) : ICommand
{
    public string Name => "state-import";

    public bool RefreshesBoard => false;

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        if (!settings.IsDatabase) throw new FactoryException("state-import refused: FACTORY_STATE is not sqlite or sql");
        if (args.Has("Azure"))
        {
            if (string.IsNullOrEmpty(settings.ContainerUrl)) throw new FactoryException("state-import -Azure refused: FACTORY_BLOB_URL (the container URL) is not set");
            sync.Download();
        }
        var text = files.ReadText(paths.BoardPath) ?? throw new FactoryException($"state-import refused: there is no {paths.BoardPath} to import");
        // Every line as written, in order; blank lines are not events (state-migrate skips them too).
        var events = (files.ReadText(paths.EventsPath) ?? "").ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var r = rows.Import(Board.Parse(text), events);

        // The exports are rewritten whole from the database: what they hold now is what was just imported.
        files.DeleteFile(Path.Combine(paths.Home, ".export.json"));
        export.Export();
        return CommandResult.Ok($"imported {r.Ideas} ideas, {r.Jobs} jobs, {r.Events} events", "check: board.json and events.jsonl match the database");
    }
}
