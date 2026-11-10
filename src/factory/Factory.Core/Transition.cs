namespace Factory.Core;

/// <summary>One row of the contract's "Job transitions" table: who may move a job between these stages, with this flag.</summary>
public sealed record Transition(string[] From, string[] To, string? Reason, string Command);
