namespace Factory.Data.Abstractions;

/// <summary>An idea's or job's row as a command read it: its JSON, the version a save checks, and its place.</summary>
public sealed record RowRead(string Json, long Version, long Ordinal);
