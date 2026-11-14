using System.Text;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>deploy &lt;id&gt;</c> (contract, "Deploy"): one attempt at the job's preview. A pass moves the job to approve; a failure
/// writes deploy-&lt;a&gt;.log, exits 1 and leaves the job in deploy, or on the fifth attempt blocks it deploy-failed. A
/// missing GitHub PAT permission gives the attempt back and blocks it github-permission. A bootstrap never deploys.
/// </summary>
public sealed class DeployCommand(IDeployService deploy, IFactoryPaths paths, IFileStore files, IDotEnv dotEnv, TimeProvider clock) : ICommand
{
    public string Name => "deploy";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var id = args.Arg(1);
        var j = session.Board.Job(id);
        Transitions.RequireStage(j, [Stages.Deploy], "deploy");
        if (j.IsBootstrap)
        {
            // Earlier attempts' record would otherwise keep showing a failed preview.
            if (j.Node["deploy"] is JsonObject old) old["status"] = "down";
            BoardMoves.Move(session, j, Stages.Approve, "orchestrator", clock);
            session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "note"), ("detail", Verdicts.BootstrapNoDeploy)));
            return CommandResult.Ok($"skipped: {Verdicts.BootstrapNoDeploy}");
        }
        var deploysOn = dotEnv.Get("HCP_ORG") is not null;
        if (!deploysOn) throw new FactoryException("deploy refused: deploys are off (no HCP_ORG in .env)");
        var d = Preview.Record(j);
        // Still applying: the last attempt's process died before it recorded a result (J-019: the devops agent's shell
        // call ended at 20 minutes, against a run of up to an hour). This resumes that attempt, not a new one.
        var resume = PsJsonReader.Is(PsJsonReader.Str(d["status"]), "applying");
        // The live preview's applied head; a failed or half-applied preview gets a full deploy.
        var since = PsJsonReader.Is(PsJsonReader.Str(d["status"]), "up") && PsJsonReader.Str(d["sha"]) is { Length: > 0 } sha ? sha : null;
        if (!resume && PsJsonReader.Int(d["attempts"]) >= Preview.MaxAttempts)
            throw new FactoryException($"deploy refused: {j.Id} has used its {Preview.MaxAttempts} attempts");
        if (!resume) d["attempts"] = PsJsonReader.Int(d["attempts"]) + 1;
        d["status"] = "applying";
        session.Save();
        var attempt = PsJsonReader.Int(d["attempts"]);

        var res = deploy.Attempt(session, j, resume, since);
        j = session.Board.Job(id);
        d = j.Node["deploy"]!.AsObject();
        d["sha"] = res.Sha;
        d["run"] = res.Run;
        if (res.WorkspaceId is not null) d["workspaceId"] = res.WorkspaceId;
        if (res.Ok)
        {
            d["status"] = "up";
            d["urls"] = res.Urls;
            d["appliedAt"] = Iso.Format(clock.GetUtcNow());
            d["destroyAt"] = res.DestroyAt;
            session.Save();
            var frontend = PsJsonReader.Str(res.Urls?["frontend"]);
            var api = PsJsonReader.Str(res.Urls?["api"]);
            session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "deploy"), ("detail", $"up {(string.IsNullOrEmpty(frontend) ? api : frontend)}")));
            BoardMoves.Move(session, j, Stages.Approve, "orchestrator", clock);
            return CommandResult.Ok($"frontend: {frontend}", $"api: {api}");
        }

        d["status"] = "failed";
        session.Save();
        var log = Path.Combine(paths.JobDir(j.Id), $"deploy-{attempt}.log");
        files.EnsureDirectory(Path.GetDirectoryName(log)!);
        files.WriteAtomic(log, new UTF8Encoding(false).GetBytes(res.Log + Environment.NewLine));
        var first = FirstError.Of(res.Log);
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "deploy"), ("detail", $"failed (attempt {attempt}): {first}")));
        if (GitHubErrors.PatNeedsPattern.Match(res.Log) is { Success: true } m)
        {
            // Not the change's fault: give the attempt back and wait for a human to add the permission.
            var needs = GitHubErrors.PatNeeds(m.Groups[1].Value);
            d["attempts"] = PsJsonReader.Int(d["attempts"]) - 1;
            BoardMoves.Move(session, j, Transitions.EscalationStage(j, deploysOn), "orchestrator", clock, "github-permission", needs);
            throw new FactoryException($"deploy refused by GitHub: {needs}; then run: factory redeploy {j.Id}");
        }
        if (attempt >= Preview.MaxAttempts) BoardMoves.Move(session, j, Transitions.EscalationStage(j, deploysOn), "orchestrator", clock, "deploy-failed", first);
        throw new FactoryException($"deploy attempt {attempt} failed: {first}; see deploy-{attempt}.log (factory deploy-log {j.Id})");
    }
}
