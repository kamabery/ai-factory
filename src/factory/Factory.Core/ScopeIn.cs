namespace Factory.Core;

/// <summary>The spec's <c>Scope: In</c> globs, and the index of the line after which a widening glob goes (-1: no In line).</summary>
public sealed record ScopeIn(IReadOnlyList<string> Globs, int At);
