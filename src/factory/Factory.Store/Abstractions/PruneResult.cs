namespace Factory.Store.Abstractions;

/// <summary>The container's size after a prune, its cap, and what the prune deleted.</summary>
public sealed record PruneResult(long Used, long Cap, int Deleted, long Freed);
