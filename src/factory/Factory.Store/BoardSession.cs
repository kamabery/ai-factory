using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>One command's hold on the board: Save-Board, Add-Event and Sync-Up, under the lock <see cref="FactoryStore"/> took.</summary>
internal sealed class BoardSession(
    IFactoryPaths paths, IFileStore files, IBoardMutex mutex, IBlobClient blobs, IStateSync sync, TimeProvider clock,
    bool azure, string? lease, JsonObject board, Func<(string? Lease, JsonObject Board)> relock) : IBoardSession
{
    private bool _released;
    private string? _lease = lease;

    public JsonObject Board { get; private set; } = board;

    public void Save()
    {
        var bytes = Encoding.UTF8.GetBytes(PsJson.Write(Board, Environment.NewLine));
        if (!azure) { files.WriteAtomic(paths.BoardPath, bytes); return; }
        if (_lease is null || !blobs.RenewLease("board", _lease))
            throw new FactoryException("state lock refused: the board lease lapsed and another machine took it");
        sync.SaveBoard(bytes);
    }

    public void AddEvent(JsonObject e)
    {
        var line = PsJson.WriteCompact(e);
        files.AppendText(paths.EventsPath, line + "\n");
        if (azure) sync.AppendEvent(line);
    }

    public void Unlocked(Action work)
    {
        Release();
        try { work(); }
        finally
        {
            // Exit-BoardLock, then Enter-BoardLock -1 and Load-Board. A refusal leaves the session released.
            (_lease, Board) = relock();
            _released = false;
        }
    }

    public void Share(string path)
    {
        if (azure) ShareOne(sync, path);
    }

    /// <summary>Pushes only <paramref name="path"/>; a failure waits for the command's own push.</summary>
    internal static void ShareOne(IStateSync sync, string path)
    {
        try { sync.Push(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)); }
        catch (FactoryException) { }
    }

    public IReadOnlyList<string> Close()
    {
        return azure ? ArtifactPush.Run(sync, Board, AddEvent, clock) : [];
    }

    public void Dispose() => Release();

    private void Release()
    {
        if (_released) return;
        _released = true;
        var l = _lease;
        _lease = null;
        if (l is not null) try { blobs.ReleaseLease("board", l); } catch (FactoryException) { }
        mutex.Exit();
    }
}
