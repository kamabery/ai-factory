using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// The push every command ends with while files are shared (contract, "State backends", Sync and Cap): the changed
/// files up, a live job's run logs held back, then the cap, noted when it deleted anything.
/// </summary>
internal static class ArtifactPush
{
    /// <returns>The warning to print when the push failed; the command itself has already done its work.</returns>
    public static IReadOnlyList<string> Run(IStateSync sync, JsonObject board, Action<JsonObject> addEvent, TimeProvider clock)
    {
        try
        {
            // A live job's run logs wait until it leaves the Building stages.
            var live = board.Jobs().Where(j => Stages.Building.Contains(j.Stage, StringComparer.OrdinalIgnoreCase)).Select(j => j.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (sync.Push(rel => SyncRules.RunLogJob(rel) is { } id && live.Contains(id)) == 0) return [];
            var r = sync.Prune(board.Jobs());
            if (r.Deleted > 0) addEvent(Events.Create(clock.GetUtcNow(), ("kind", "note"), ("detail", $"pruned {r.Deleted} blobs, {r.Freed} bytes freed")));
            return [];
        }
        catch (FactoryException e)
        {
            return [$"factory: state push failed: {e.Message}"];
        }
    }
}
