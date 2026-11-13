namespace Factory.Core;

/// <summary>factory.ps1's deploy-loop refusals (contract, "Deploy", "The deploy loop").</summary>
public static class DeployRules
{
    /// <summary>
    /// Require-FailedDeploy: <c>deploy-fix</c> and <c>block</c> need the job in deploy with its last attempt failed, so an
    /// agent cannot skip the deploy.
    /// </summary>
    public static void RequireFailedDeploy(Job job, string command)
    {
        Transitions.RequireStage(job, [Stages.Deploy], command);
        if (!PsJsonReader.Is(job.Deploy?.Status, "failed")) throw new FactoryException($"{command} refused: {job.Id} has no failed deploy attempt");
    }

    /// <summary>
    /// redeploy's rule: a job blocked from deploy (whatever its flag: the human fixed the cause), or one in ready that left
    /// deploy before blocked existed (flagged deploy-failed, escalated or github-permission), or flagged github-permission.
    /// </summary>
    public static void RequireRedeployable(Job job)
    {
        Transitions.RequireStage(job, [Stages.Ready, Stages.Blocked], "redeploy");
        var last = job.Node["history"] is System.Text.Json.Nodes.JsonArray h && h.Count > 0 ? h[^1] : null;
        var from = PsJsonReader.Str(last?["from"]);
        var fromDeploy = PsJsonReader.Is(from, Stages.Deploy);
        var reason = job.Flag?.Reason;
        var legacy = fromDeploy && PsJsonReader.In(reason, "deploy-failed", "escalated", "github-permission");
        if (PsJsonReader.Is(job.Stage, Stages.Blocked) && !fromDeploy)
            throw new FactoryException($"redeploy refused: {job.Id} was blocked from {from}, not deploy; it never had a passing round to deploy (rework or discard it)");
        if (PsJsonReader.Is(job.Stage, Stages.Ready) && !legacy && !PsJsonReader.Is(reason, "github-permission"))
            throw new FactoryException($"redeploy refused: {job.Id} is neither flagged github-permission nor came to ready from deploy (deploy-failed or escalated)");
    }
}
