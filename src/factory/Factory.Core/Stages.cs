namespace Factory.Core;

/// <summary>Stages, roles and the Building column (contract, "Board columns and stages").</summary>
public static class Stages
{
    public const string Backlog = "backlog", Approved = "approved", Canary = "canary", Build = "build", Review = "review", Deploy = "deploy",
        Approve = "approve", Gate = "gate", Blocked = "blocked", Ready = "ready", Done = "done", Discarded = "discarded";

    public static readonly string[] All = [Backlog, Approved, Canary, Build, Review, Deploy, Approve, Gate, Blocked, Ready, Done, Discarded];

    /// <summary>The stages that count as building: one job at a time. <c>blocked</c> is in the column but not here.</summary>
    public static readonly string[] Building = [Build, Review, Deploy, Approve, Gate];

    public static readonly string[] Roles = ["security", "code", "ux", "ui"];
}
