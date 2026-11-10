using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>
/// The verdict headers (contract, "Files"): a review's first line is exactly <c>Verdict: PASS</c> or
/// <c>Verdict: CHANGES</c>, a decision's <c>Decision: APPROVE</c> or <c>Decision: ESCALATE</c>, and an
/// evaluation's <c>Rating: good|mixed|poor</c>. Anything else is no verdict.
/// </summary>
public static partial class Verdicts
{
    public const string BootstrapNoDeploy =
        "deploy skipped: bootstrap (deploy.yml reaches main only when this merges; the first feature job gets the first preview)";

    public static string? Review(string? firstLine) => firstLine switch
    {
        "Verdict: PASS" => "PASS",
        "Verdict: CHANGES" => "CHANGES",
        _ => null,
    };

    public static string? Rating(string? firstLine) =>
        firstLine is not null && RatingLine().Match(firstLine) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// <c>read-reviews</c>: records each role's verdict from its review's first line, then moves the job:
    /// all PASS to deploy (deployable) or approve, CHANGES at round 1 to a new build round, CHANGES at round 2
    /// to ready or blocked with <c>max-rounds</c>. A missing verdict exits 2 and keeps the verdicts it read.
    /// </summary>
    public static IReadOnlyList<JsonObject> ReadReviews(Job job, Func<string, string?> firstLineOfReview, bool deploysOn, DateTimeOffset now)
    {
        Transitions.RequireStage(job, [Stages.Review], "read-reviews");
        if (job.Node["reviews"] is not JsonObject reviews) job.Node["reviews"] = reviews = [];
        var missing = new List<string>();
        foreach (var role in Stages.Roles)
        {
            if (Review(firstLineOfReview(role)) is { } v) reviews[role] = v;
            else missing.Add(role);
        }
        if (missing.Count > 0) throw new FactoryException($"no verdict from: {string.Join(", ", missing)}", 2) { KeepChanges = true };

        var changes = Stages.Roles.Where(r => Json.Is(Json.Str(reviews[r]), "CHANGES")).ToList();
        if (changes.Count == 0)
        {
            if (job.IsBootstrap)
                return [Transitions.Move(job, Stages.Approve, "orchestrator", null, null, null, now), Note(job, BootstrapNoDeploy, now)];
            if (deploysOn) return [Transitions.Move(job, Stages.Deploy, "orchestrator", null, null, null, now)];
            return [Transitions.Move(job, Stages.Approve, "orchestrator", null, null, null, now), Note(job, "deploy skipped: no HCP_ORG in .env", now)];
        }
        if (job.Round < 2)
        {
            job.Node["round"] = job.Round + 1;
            Transitions.ResetReviews(job);
            return [Transitions.Move(job, Stages.Build, "orchestrator", null, null, null, now)];
        }
        return [Transitions.Move(job, Transitions.EscalationStage(job, deploysOn), "orchestrator", "max-rounds",
            $"CHANGES from {string.Join(", ", changes)} at round {job.Round}", null, now)];
    }

    /// <summary>
    /// <c>read-decision</c>: APPROVE moves the job to gate; ESCALATE to ready with <c>escalated</c>, the
    /// decision's <c>## Escalation reason</c> as the flag's <c>why</c> and its first line as <c>detail</c>.
    /// A missing or malformed decision line exits 2.
    /// </summary>
    public static JsonObject ReadDecision(Job job, string? decisionText, DateTimeOffset now)
    {
        Transitions.RequireStage(job, [Stages.Approve], "read-decision");
        switch (SpecText.FirstLine(decisionText))
        {
            case "Decision: APPROVE":
                job.Node["decision"] = "APPROVE";
                return Transitions.Move(job, Stages.Gate, "orchestrator", null, null, null, now);
            case "Decision: ESCALATE":
                job.Node["decision"] = "ESCALATE";
                var why = SpecText.Section(decisionText, "Escalation reason");
                var detail = why is null ? "approver escalated; see decision.md" : why.Split('\n')[0].Trim();
                return Transitions.Move(job, Stages.Ready, "orchestrator", "escalated", detail, why, now);
            default:
                throw new FactoryException("no decision line in decision.md", 2);
        }
    }

    static JsonObject Note(Job job, string detail, DateTimeOffset now) =>
        Events.Create(now, ("job", job.Id), ("kind", "note"), ("detail", detail));

    [GeneratedRegex("^Rating: (good|mixed|poor)$")] private static partial Regex RatingLine();
}
