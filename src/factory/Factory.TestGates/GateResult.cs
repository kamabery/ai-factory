namespace Factory.TestGates;

/// <summary>One assembly's line coverage against its gate.</summary>
public sealed record GateResult(string Assembly, double Rate, double Gate, bool Ok);
