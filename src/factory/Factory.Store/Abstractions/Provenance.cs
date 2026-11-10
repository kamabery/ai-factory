namespace Factory.Store.Abstractions;

/// <summary>The template-sourced paths a round changed (null when the round has no recorded start), and a note on how they were judged.</summary>
public sealed record Provenance(IReadOnlyList<string>? Paths, string? Note);
