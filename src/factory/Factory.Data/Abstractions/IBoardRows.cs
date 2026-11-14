using System.Text.Json.Nodes;

namespace Factory.Data.Abstractions;

/// <summary>
/// The board and events in the database (contract, "State backends", Database). Every call applies any pending
/// migration first. Refusals are <see cref="Core.FactoryException"/>s with the contract's one-line reasons.
/// </summary>
public interface IBoardRows
{
    /// <summary>The board with its row versions, or null when the database has no board yet.</summary>
    BoardRead? Read();

    /// <summary>
    /// Writes what changed since <paramref name="read"/>, in one transaction, each row only if its version is still the
    /// one read, and bumps the revision. Returns what a later save checks against: the same board, the new versions.
    /// </summary>
    BoardRead Save(BoardRead read, JsonObject board);

    /// <summary>Appends one events line (as written, without its newline) and bumps the revision.</summary>
    void AddEvent(string line);

    /// <summary>The board row's revision; 0 when there is no board.</summary>
    long Revision();

    /// <summary>The events above <paramref name="seq"/>, in order.</summary>
    IReadOnlyList<EventLine> EventsAfter(long seq);

    /// <summary>
    /// The one-time move of a board and its events lines into an empty database, in one transaction that rolls back
    /// unless the board and events read back from the new rows equal the source.
    /// </summary>
    ImportResult Import(JsonObject board, IReadOnlyList<string> events);
}
