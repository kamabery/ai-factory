namespace Factory.Store.Abstractions;

/// <summary>An appended block's new ETag, and the offset it landed at.</summary>
public sealed record AppendResult(string ETag, long Offset);
