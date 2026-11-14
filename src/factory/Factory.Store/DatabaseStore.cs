using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Data.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>Opens the board of a sqlite or sql backend: no board lock, the board read with its row versions.</summary>
public sealed class DatabaseStore(IFactoryPaths paths, IFileStore files, IBoardRows rows, IStateExport export)
{
    /// <param name="refresh">False only for state-import, which runs where the database has no board yet.</param>
    public IBoardSession Open(bool refresh = true)
    {
        var read = rows.Read();
        if (read is null && refresh && files.Stat(paths.BoardPath) is not null)
            throw new FactoryException($"state: the database has no board but {paths.BoardPath} exists; run factory state-import");
        return new DbBoardSession(rows, export, read ?? BoardRead.None());
    }
}
