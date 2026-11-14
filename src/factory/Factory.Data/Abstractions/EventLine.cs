namespace Factory.Data.Abstractions;

/// <summary>One stored events line and its place in the order.</summary>
public sealed record EventLine(long Seq, string Line);
