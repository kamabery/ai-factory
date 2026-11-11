using Factory.Core;

namespace Factory.Store.Abstractions;

/// <summary>Keeps FACTORY_HOME and the container in step (contract, "State backends", Sync and Cap).</summary>
public interface IStateSync
{
    /// <summary>Refreshes board.json from the container with If-None-Match.</summary>
    BoardSync SyncBoard();

    /// <summary>Downloads every synced blob under <paramref name="prefix"/> whose ETag changed, unless the local file changed. Returns how many.</summary>
    int Pull(string prefix = "");

    /// <summary>
    /// Uploads every synced file changed since the manifest, except where <paramref name="skip"/> says so, and
    /// deletes the blob of every manifest path gone locally. Returns how many files it uploaded.
    /// </summary>
    int Push(Func<string, bool>? skip = null);

    /// <summary>Writes board.json to the container (If-Match the ETag it was read at), then locally.</summary>
    void SaveBoard(byte[] bytes);

    /// <summary>Appends one event line (without its newline) to this month's blob.</summary>
    void AppendEvent(string line);

    /// <summary>Rolls the container back under its cap, in the contract's order.</summary>
    PruneResult Prune(IEnumerable<Job> jobs);

    /// <summary>
    /// Sync-Events: rebuilds events.jsonl from the month blobs when this month's changed, or on a cache that has never
    /// listed them. True when it rewrote the file.
    /// </summary>
    bool SyncEvents();

    /// <summary>
    /// Invoke-StateMigrate: the one-time upload of an existing FACTORY_HOME, events by month, then the files, then the
    /// board (last, so an interrupted migrate can run again). Returns how many blobs it wrote.
    /// </summary>
    int Migrate();
}
