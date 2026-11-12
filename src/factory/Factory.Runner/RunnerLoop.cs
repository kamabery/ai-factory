using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Core;
using Factory.Runner.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Runner;

/// <summary>
/// runner.ps1: drains the Approved queue one job at a time, each in its own temp clone (contract, "Runner"). One job's
/// failure never stalls the queue: it is logged, the job escalated, and the next taken. A usage limit stops the drain.
/// Returns how the drain ended: drained, usage-limit, fatal: ..., or locked when another runner holds the queue.
/// </summary>
public sealed partial class RunnerLoop(
    IRunnerLock runnerLock, IFactoryCommands factory, IClaudeRunner claude, ILlmProvider provider, IRunnerLog log, IFactoryPaths paths,
    Core.Abstractions.IFileStore files)
{
    private static readonly string[] Building = [Stages.Build, Stages.Review, Stages.Deploy, Stages.Approve, Stages.Gate];
    private static readonly string[] Resumable = [Stages.Build, Stages.Review, Stages.Deploy, Stages.Approve];

    public string Run()
    {
        using var held = runnerLock.TryTake();
        if (held is null) return "locked";
        log.Write($"start pid={Environment.ProcessId}");
        var how = "stopped";
        try { how = Drain(); }
        catch (Exception e)
        {
            how = $"fatal: {e.Message}";
            log.Write(e.StackTrace ?? "");
        }
        finally { log.Write($"exit {how}"); }
        return how;
    }

    private string Drain()
    {
        var canaryRan = false;
        while (true)
        {
            // A deployable job waits for a canary pass on the current deploy inputs (contract, "Canary"). One run per
            // drain: a failure leaves those jobs in Approved for the human.
            var id = (canaryRan ? factory.Run("next") : factory.Run("next", "-NeedsCanary")).Out;
            if (id == "canary")
            {
                canaryRan = true;
                var c = factory.Run("canary");
                log.Write(c.Code == 0 ? "canary pass" : $"canary failed: {CanaryPrefix().Replace(c.Err, "")}");
                continue;
            }
            if (id.Length == 0)
            {
                // Previews that outlived their work; a failure here must not read as a failed drain.
                var sw = factory.Run("sweep");
                if (sw.Code != 0) log.Write($"sweep failed: {sw.Err}");
                else if (sw.Out.Length > 0) log.Write($"sweep started: {NewLines().Replace(sw.Out, " ")}");
                return "drained";
            }

            // A redeployed job resumes at deploy, and a resumed one at the stage a usage limit stopped it in, continuing
            // that claude session; everything else starts at build.
            var sj = StatusJob(id);
            var resume = PsJsonReader.Str(sj?["resume"]);
            var session = PsJsonReader.Str(sj?["resumeSession"]) ?? "";
            var to = PsJsonReader.In(resume, Resumable) ? resume! : Stages.Build;
            if (factory.Run("set-stage", id, to, "-By", "runner").Code != 0) return $"fatal: set-stage {id} {to} refused";
            log.Write($"take {id}");
            try
            {
                if (TakeJob(id, session) is { } stop) return stop;
            }
            catch (Exception e)
            {
                log.Write($"error {id}: {e.Message}\n{e.StackTrace}");
                if (PsJsonReader.In(Stage(id), Building)) Escalate(id, $"runner error: {e.Message}");
            }
            finally { log.Write($"stage {id} {Stage(id)}"); }
        }
    }

    /// <summary>One job's pass. Returns how the drain ends when it must stop (a usage limit), else null.</summary>
    private string? TakeJob(string id, string session)
    {
        // The run mode decides the provider; a job's own provider field (older factories) is ignored.
        IReadOnlyDictionary<string, string?> childEnv;
        try { childEnv = provider.ChildEnvironment(); }
        catch (FactoryException e)
        {
            log.Write($"provider {id} failed: provider: {e.Message}");
            Escalate(id, $"provider: {e.Message}");
            return null;
        }

        var co = factory.Run("checkout", id);
        if (co.Code != 0)
        {
            log.Write($"checkout {id} failed: {co.Err}");
            Escalate(id, $"work dir: {co.Err}");
            return null;
        }
        var wt = co.Out;
        var jobDir = paths.JobDir(id);
        var n = files.Files(jobDir).Count(f => Path.GetDirectoryName(f.Path) == Path.GetFullPath(jobDir) && RunLog().IsMatch(Path.GetFileName(f.Path))) + 1;
        var runLog = $"run-{n}.jsonl";
        var state = paths.Home.Replace('\\', '/');
        var runCmd = $"/factory:factory run {id} {state} {Path.Combine(paths.ScriptsDir, "factory.ps1").Replace('\\', '/')}";
        var prompt = session.Length > 0
            ? $"A usage limit stopped this run, and it has reset. Continue `{runCmd}` from where you stopped: read the job's stage with `status -Json` first, and re-run the current step's agent if its file is missing."
            : runCmd;
        var run = claude.Run(id, prompt, wt, Path.Combine(jobDir, runLog), childEnv, session);
        var result = run.Result;
        log.Write($"claude {id} exit={run.Code} result={(result is null ? "no" : "yes")}");

        if (result is not null && result.ContainsKey("total_cost_usd"))
        {
            var ms = result.ContainsKey("duration_ms") ? PsJsonReader.Text(result["duration_ms"]) : "0";
            // A refused cost is an error finishing the job.
            var cost = factory.Run("set-cost", id, PsJsonReader.Text(result["total_cost_usd"]), ms);
            if (cost.Code != 0) throw new InvalidOperationException($"set-cost {id} refused (exit {cost.Code})");
        }

        // A usage limit (the subscription's, or a Foundry 429) is no one's decision: flag it with the session to
        // continue, and stop, since the next job would hit the same limit. next holds the queue.
        var limited = result is not null && (PsJsonReader.Is(PsJsonReader.Str(result["api_error"]), "usage_limit_reached") || PsJsonReader.Text(result["api_error_status"]) == "429");
        if (limited && PsJsonReader.In(Stage(id), Resumable) && result!.ContainsKey("session_id"))
        {
            var text = result.ContainsKey("result") ? PsJsonReader.Text(result["result"]) : "usage limit";
            if (factory.Run("set-stage", id, "ready", "-By", "runner", "-Reason", "usage-limit", "-Detail", text, "-Session", PsJsonReader.Text(result["session_id"])).Code == 0)
                return "usage-limit";
        }

        // The approver approved and only the gate is left; run it rather than escalate.
        if (PsJsonReader.Is(Stage(id), Stages.Gate)) factory.Run("gate", id);

        if (PsJsonReader.In(Stage(id), Building))
        {
            var why = run.Code != 0 ? $"claude exited with exit {run.Code}" : "claude finished but the job is still in Building";
            if (result is not null && PsJsonReader.Truthy(result["is_error"]) && result.ContainsKey("result")) why += $": {PsJsonReader.Text(result["result"])}";
            Escalate(id, $"{why}; see {runLog}");
        }

        // Push the branch and open the PR, so the human reviews it on GitHub. merge publishes again. escalated lands a
        // deployable job in blocked, which is published too.
        if (PsJsonReader.In(Stage(id), Stages.Ready, Stages.Blocked))
        {
            var pub = factory.Run("publish", id);
            if (pub.Code != 0) log.Write($"publish {id}: {pub.Err}");
            // What a job that went bad taught, for the templates; a clean job writes nothing.
            var les = factory.Run("lesson", id);
            if (les.Code != 0) log.Write($"lesson {id}: {les.Err}");
        }
        return null;
    }

    private void Escalate(string id, string detail) => factory.Run("set-stage", id, "ready", "-By", "runner", "-Reason", "escalated", "-Detail", detail);

    private JsonObject? StatusJob(string id)
    {
        try
        {
            return (JsonNode.Parse(factory.Run("status", "-Json").Out)?["jobs"] as JsonArray)?
                .OfType<JsonObject>().FirstOrDefault(j => PsJsonReader.Is(PsJsonReader.Str(j["id"]), id));
        }
        catch (JsonException) { return null; }
    }

    private string Stage(string id) => PsJsonReader.Text(StatusJob(id)?["stage"]);

    [GeneratedRegex(@"^run-.*\.jsonl$", RegexOptions.IgnoreCase)] private static partial Regex RunLog();
    [GeneratedRegex("^factory: canary failed: ")] private static partial Regex CanaryPrefix();
    [GeneratedRegex(@"\r?\n")] private static partial Regex NewLines();
}
