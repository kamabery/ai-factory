using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Data.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// One command's hold on a database board: no lock, each <see cref="Save"/> a version-checked transaction, each event an
/// insert, and the exports brought up to date after each and at the end (contract, "State backends", Database). With
/// <paramref name="shared"/>, the artifacts are pushed at the end.
/// </summary>
internal sealed class DbBoardSession(IBoardRows rows, IStateExport export, BoardRead read, IStateSync? shared, TimeProvider clock) : IBoardSession
{
    private BoardRead _read = read;

    public JsonObject Board => _read.Board;

    public void Save()
    {
        _read = rows.Save(_read, _read.Board);
        export.Export();
    }

    public void AddEvent(JsonObject e)
    {
        rows.AddEvent(PsJson.WriteCompact(e));
        export.Export();
    }

    // Nothing to release: the long step runs, then the board is read again, as it is after the lock is re-taken.
    public void Unlocked(Action work)
    {
        try { work(); }
        finally { _read = rows.Read() ?? BoardRead.None(); }
    }

    public IReadOnlyList<string> Close()
    {
        var warnings = shared is null ? [] : ArtifactPush.Run(shared, Board, AddEvent, clock);
        export.Export();
        return warnings;
    }

    public void Dispose() { }
}
