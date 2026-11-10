using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>The contract's job transitions, as data, and the one way a job changes stage.</summary>
public static class Transitions
{
    static readonly string[] ReadyOrBlocked = [Stages.Ready, Stages.Blocked];

    /// <summary>
    /// Every allowed move (contract, "Job transitions"). <c>Reason</c> is the flag the move sets; null clears it.
    /// Where the contract says "ready / blocked" the stage comes from <see cref="EscalationStage"/>.
    /// </summary>
    public static readonly Transition[] Rows =
    [
        new([Stages.Backlog], [Stages.Approved], null, "approve"),
        new([Stages.Backlog], [Stages.Discarded], null, "discard"),
        new([Stages.Approved], [Stages.Build], null, "set-stage"),
        new([Stages.Build], [Stages.Review], null, "set-stage"),
        new([Stages.Build], ReadyOrBlocked, "out-of-scope", "set-stage"),
        new([Stages.Build], ReadyOrBlocked, "spec-stale", "set-stage"),
        new([Stages.Review], [Stages.Build], null, "read-reviews"),
        new([Stages.Review], ReadyOrBlocked, "max-rounds", "read-reviews"),
        new([Stages.Review], [Stages.Deploy], null, "read-reviews"),
        new([Stages.Review], [Stages.Approve], null, "read-reviews"),
        new([Stages.Deploy], [Stages.Approve], null, "deploy"),
        new([Stages.Deploy], [Stages.Build], null, "deploy-fix"),
        new([Stages.Deploy], ReadyOrBlocked, "deploy-fix-rounds", "deploy-fix"),
        new([Stages.Deploy], ReadyOrBlocked, "deploy-failed", "deploy"),
        new([Stages.Deploy], ReadyOrBlocked, "environment", "block"),
        new([Stages.Deploy], ReadyOrBlocked, "github-permission", "deploy"),
        new([Stages.Blocked, Stages.Ready], [Stages.Approved], null, "redeploy | rework | resume"),
        new([Stages.Blocked], [Stages.Discarded], null, "discard"),
        new([Stages.Approved], [Stages.Deploy], null, "set-stage (redeploy)"),
        new([Stages.Approved], [Stages.Build, Stages.Review, Stages.Deploy, Stages.Approve], null, "set-stage (resume)"),
        new([Stages.Approve], [Stages.Gate], null, "read-decision"),
        new([Stages.Approve], [Stages.Ready], "escalated", "read-decision"),
        new([Stages.Gate], [Stages.Ready], null, "gate"),
        new([Stages.Gate], [Stages.Ready], "gate-failed", "gate"),
        new([Stages.Build, Stages.Review, Stages.Deploy], [Stages.Blocked], "escalated", "set-stage"),
        new([Stages.Build, Stages.Review, Stages.Deploy, Stages.Approve, Stages.Gate], [Stages.Ready], "escalated", "set-stage | gate"),
        new([Stages.Build, Stages.Review, Stages.Deploy, Stages.Approve], ReadyOrBlocked, "usage-limit", "set-stage"),
        new([Stages.Ready], [Stages.Done], null, "merge | sync-prs"),
        new([Stages.Ready], [Stages.Ready], "merge-blocked", "merge"),
        new([Stages.Ready], [Stages.Ready], "gate-failed", "merge"),
        new([Stages.Ready], [Stages.Discarded], null, "discard"),
    ];

    public static bool Allows(string from, string to, string? reason)
    {
        if (string.IsNullOrEmpty(reason)) reason = null;
        return Rows.Any(r => PsJsonReader.In(from, r.From) && PsJsonReader.In(to, r.To) && r.Reason == reason);
    }

    /// <summary>
    /// factory.ps1's Move-Job: sets the stage, <c>updatedAt</c> and <c>flag</c> (a <c>why</c> only with a
    /// reason), clears <c>roundStart</c> on a move into build, appends to <c>history</c>, and returns the
    /// <c>stage</c> event. A move the table does not allow is refused and changes nothing.
    /// </summary>
    public static JsonObject Move(Job job, string to, string by, string? reason, string? detail, string? why, DateTimeOffset now)
    {
        var from = job.Stage;
        if (!Allows(from, to, reason))
            throw new FactoryException($"transition {from}->{to} refused (use the dedicated command, or see FACTORY-CONTRACT.md)");
        var ts = Iso.Format(now);
        var n = job.Node;
        n["stage"] = to;
        n["updatedAt"] = ts;
        if (string.IsNullOrEmpty(reason)) n["flag"] = null;
        else
        {
            // factory.ps1's [string] parameters make a missing detail "" and a missing why "", so no why.
            var flag = new JsonObject { ["reason"] = reason, ["detail"] = detail ?? "" };
            if (!string.IsNullOrEmpty(why)) flag["why"] = why;
            n["flag"] = flag;
        }
        // Every move into build starts a round; checkout records the new round's start.
        if (PsJsonReader.Is(to, Stages.Build) && PsJsonReader.Truthy(n["roundStart"])) n["roundStart"] = null;
        // @($j.history) + entry: a job with no history array gets a leading null.
        var history = n["history"] is JsonArray old ? new JsonArray([.. old.Select(h => h?.DeepClone())]) : new JsonArray((JsonNode?)null);
        history.Add(new JsonObject { ["ts"] = ts, ["from"] = from, ["to"] = to, ["by"] = by });
        n["history"] = history;

        var e = Events.Create(now, ("job", job.Id), ("kind", "stage"), ("from", from), ("to", to), ("by", by));
        if (!string.IsNullOrEmpty(reason)) e["reason"] = reason;
        return e;
    }

    /// <summary>
    /// Where a job the factory gives up on waits (contract, "Blocked"): a deployable job that never reached
    /// a passing preview parks in blocked; one in approve or gate has its preview up, so it goes to ready.
    /// </summary>
    public static string EscalationStage(Job job, bool deploysOn) =>
        IsDeployable(job, deploysOn) && PsJsonReader.In(job.Stage, Stages.Build, Stages.Review, Stages.Deploy) ? Stages.Blocked : Stages.Ready;

    /// <summary>Deploys are on (<c>HCP_ORG</c>) and the job is not a bootstrap.</summary>
    public static bool IsDeployable(Job job, bool deploysOn) => deploysOn && !job.IsBootstrap;

    public static void RequireStage(Job job, string[] stages, string what)
    {
        if (!PsJsonReader.In(job.Stage, stages))
            throw new FactoryException($"{what} refused: {job.Id} is in '{job.Stage}', needs {string.Join('|', stages)}");
    }

    /// <summary>Every review back to <c>pending</c>, as a new round starts.</summary>
    public static void ResetReviews(Job job) =>
        job.Node["reviews"] = new JsonObject { ["security"] = "pending", ["code"] = "pending", ["ux"] = "pending", ["ui"] = "pending" };
}
