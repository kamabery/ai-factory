namespace Factory.Data.Abstractions;

/// <summary>An events line's keys, copied into its row for queries; null where the line has none (or is not JSON).</summary>
public sealed record EventColumns(string? Ts, string? Job, string? Idea, string? Kind);
