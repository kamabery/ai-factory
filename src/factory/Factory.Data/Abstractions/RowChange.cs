using System.Text.Json.Nodes;

namespace Factory.Data.Abstractions;

/// <summary>One idea or job row a save writes: inserted, updated or deleted, and the version it was read at.</summary>
public sealed record RowChange(RowKind Kind, ChangeKind Change, string Id, string? Json, long Ordinal, long ReadVersion, JsonObject? Node);
