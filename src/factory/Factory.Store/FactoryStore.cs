using System.Globalization;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// factory.ps1's board lock and Load-Board (contract, "factory.ps1 commands" and "State backends"): the mutex, then
/// in azure the lease on <c>locks/board</c> and a refresh from the container, then board.json.
/// </summary>
public sealed class FactoryStore(
    IFactoryPaths paths, IFileStore files, IBoardMutex mutex, IStateSettings settings, IOptions<StateOptions> options,
    IBlobClient blobs, IStateSync sync, TimeProvider clock, DatabaseStore database) : IFactoryStore
{
    public string TenantId => "default";

    public IBoardSession Open(bool refresh = true)
    {
        files.EnsureDirectory(paths.Home);   // factory.ps1 creates FACTORY_HOME on every command
        // sqlite and sql: no mutex and no lease; each save is a version-checked transaction instead.
        if (settings.IsDatabase) return database.Open(refresh);
        var seconds = Seconds(options.Value.BoardLockSeconds) ?? 30;
        var (lease, board) = Lock(seconds, Seconds(options.Value.StateLockSeconds) ?? seconds, refresh);
        // Re-taking it after a gate or a clone waits as long as it takes; in azure the lease waits 600 s.
        return new BoardSession(paths, files, mutex, blobs, sync, clock, settings.IsAzure, lease, board,
            () => Lock(-1, Seconds(options.Value.StateLockSeconds) ?? 600, refresh));
    }

    private (string? Lease, JsonObject Board) Lock(int seconds, int leaseWait, bool refresh)
    {
        // Timing out must refuse: running on unlocked, this command's save could overwrite another's.
        if (!mutex.Enter(seconds)) throw new FactoryException($"board lock refused: another factory has held the board for over {seconds} s");
        string? lease = null;
        try
        {
            if (settings.IsAzure)
            {
                lease = blobs.AcquireLease("board", leaseWait)
                    ?? throw new FactoryException($"state lock refused: another machine has held the board (locks/board) for over {leaseWait} s");
                // The container's board wins; when it changed, so may everything else.
                if (refresh && sync.SyncBoard() == BoardSync.Changed) sync.Pull();
            }
            var text = files.ReadText(paths.BoardPath);
            return (lease, text is null ? Board.Empty() : Board.Parse(text));
        }
        catch
        {
            if (lease is not null) try { blobs.ReleaseLease("board", lease); } catch (FactoryException) { }
            mutex.Exit();
            throw;
        }
    }

    private static int? Seconds(string? s) => string.IsNullOrEmpty(s) ? null : int.Parse(s, CultureInfo.InvariantCulture);
}
