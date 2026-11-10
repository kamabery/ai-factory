using System.Globalization;
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
    IBlobClient blobs, IStateSync sync, TimeProvider clock) : IFactoryStore
{
    public string TenantId => "default";

    public IBoardSession Open()
    {
        files.EnsureDirectory(paths.Home);   // factory.ps1 creates FACTORY_HOME on every command
        var azure = settings.IsAzure;
        var seconds = Seconds(options.Value.BoardLockSeconds) ?? 30;
        // Timing out must refuse: running on unlocked, this command's save could overwrite another's.
        if (!mutex.Enter(seconds)) throw new FactoryException($"board lock refused: another factory.ps1 has held the board for over {seconds} s");
        string? lease = null;
        try
        {
            if (azure)
            {
                var wait = Seconds(options.Value.StateLockSeconds) ?? seconds;
                lease = blobs.AcquireLease("board", wait)
                    ?? throw new FactoryException($"state lock refused: another machine has held the board (locks/board) for over {wait} s");
                // The container's board wins; when it changed, so may everything else.
                if (sync.SyncBoard() == BoardSync.Changed) sync.Pull();
            }
            var text = files.ReadText(paths.BoardPath);
            return new BoardSession(paths, files, mutex, blobs, sync, clock, azure, lease, text is null ? Board.Empty() : Board.Parse(text));
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
