using System.Text.Json.Nodes;

namespace Factory.Store.Abstractions;

/// <summary>One command's hold on the board, from its lock to its push. Dispose releases the lock.</summary>
public interface IBoardSession : IDisposable
{
    JsonObject Board { get; }

    /// <summary>Writes the board: atomically to board.json, and in azure to the container under the lease.</summary>
    void Save();

    /// <summary>Appends one events.jsonl line, <c>ts</c> already first; in azure also to this month's blob.</summary>
    void AddEvent(JsonObject e);

    /// <summary>
    /// How every command ends, still under the lock: in azure, push what changed and hold the container to its
    /// cap. Returns the stderr lines a failed push leaves (the command's own result stands).
    /// </summary>
    IReadOnlyList<string> Close();
}
