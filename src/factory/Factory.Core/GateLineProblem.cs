namespace Factory.Core;

/// <summary>Why a gate line needs a shell, and how to rewrite it (contract, "Gate"; approve's refusal).</summary>
public sealed record GateLineProblem(string Reason, string Hint);
