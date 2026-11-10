namespace Factory.Core;

/// <summary>Why <c>merge</c> cannot go ahead: the flag's detail (<c>merge-blocked</c>) and the refusal it prints.</summary>
public sealed record MergeBlock(string Detail, string Refusal);
