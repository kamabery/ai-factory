using System.Text.Json.Nodes;

namespace Factory.Data.Abstractions;

/// <summary>
/// The board as a command read it from the database, with what its save checks: the board row's <see cref="Version"/>
/// (0: there was no board row) and each idea's and job's row, by id.
/// </summary>
public sealed record BoardRead(
    JsonObject Board, string? Skeleton, long Version, IReadOnlyDictionary<string, RowRead> Ideas, IReadOnlyDictionary<string, RowRead> Jobs)
{
    /// <summary>A database with no board yet: the empty board, and nothing read.</summary>
    public static BoardRead None() => new(Core.Board.Empty(), null, 0, new Dictionary<string, RowRead>(), new Dictionary<string, RowRead>());
}
