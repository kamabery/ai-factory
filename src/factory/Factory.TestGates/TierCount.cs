namespace Factory.TestGates;

/// <summary>One tier's run: how many tests it had, and how many passed.</summary>
public sealed record TierCount(string Tier, int Total, int Passed);
