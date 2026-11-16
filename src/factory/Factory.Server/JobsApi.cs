using System.Text.Json.Nodes;
using Factory.Core.Abstractions;
using Factory.Server.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Server;

/// <summary>
/// The job endpoints of the contract's "Dashboard API": each write is one factory.ps1 call, and this class holds no
/// transition logic of its own. "Starts the runner" means runner.ps1, detached; the runner itself refuses a second copy.
/// </summary>
public sealed class JobsApi(
    IFactoryCalls factory, IFactoryLauncher launcher, IRunnerStarter runner, IFactoryPaths paths, IFileStore files, BoardFile board, IStateCache cache, IProcessProbe procs)
{
    public Reply Files(string id)
    {
        cache.Job(id);
        var dir = Path.GetFullPath(paths.JobDir(id));
        var list = files.Files(dir)
            .Select(f => Path.GetRelativePath(dir, f.Path).Replace('\\', '/'))
            .Order(StringComparer.InvariantCultureIgnoreCase);
        return Reply.Data(new JsonArray([.. list.Select(p => (JsonNode)p)]));
    }

    /// <summary>The transcript file named by that agent's last agent-start event; no other path is served.</summary>
    public Reply Transcript(string agentId)
    {
        var events = files.ReadText(paths.EventsPath);
        var needle = $"\"agentId\":\"{agentId}\"";
        var line = events?.Split('\n').LastOrDefault(l => l.Contains("\"agent-start\"", StringComparison.Ordinal) && l.Contains(needle, StringComparison.Ordinal));
        var start = line is null ? null : JsonNode.Parse(line) as JsonObject;
        var transcript = BoardFile.Str(start, "transcript");
        if (transcript is null) return Reply.NotFound("no such agent");
        // Run on another machine: the copy its agent-stop took into the job's folder.
        if (files.Stat(transcript) is null && BoardFile.Str(start, "job") is { } job)
        {
            cache.Job(job);
            return Reply.File(Path.Combine(paths.JobDir(job), "transcripts", $"agent-{agentId}.jsonl"));
        }
        return Reply.File(transcript);
    }

    public Reply EditSpec(string id, string markdown) =>
        ServerFiles.WithTempFile(paths.Home, $"spec-edit-{procs.CurrentPid}.md", markdown, tmp => factory.Run("edit-spec", id, "-File", tmp), "spec saved");

    public Reply Move(string id, string body)
    {
        var req = JsonNode.Parse(body) as JsonObject;
        var to = BoardFile.Str(req, "to");
        var note = BoardFile.Str(req, "note");
        var job = board.Job(id);
        if (job is null) return Reply.NotFound("no such job");
        var r = to switch
        {
            "approved" when BoardFile.Str(job, "stage") is "ready" or "blocked" =>
                string.IsNullOrEmpty(note) ? CallResult.Refuse("rework refused: a rework needs a note") : factory.Run("rework", id, note),
            "approved" => factory.Run("approve", id),
            "done" => factory.Run("merge", id),
            "discarded" => factory.Run("discard", id),
            _ => CallResult.Refuse($"move to '{to}' refused: not a human move"),
        };
        if (r.Ok && to == "approved") StartRunner();
        return Reply.Of(r);
    }

    /// <summary>Resume on a card stopped by a usage limit: back to Approved, to continue its claude session.</summary>
    public Reply Resume(string id) => RunThenStartRunner("resume", id);

    /// <summary>Retry deploy on a blocked card: back to Approved, resuming at deploy.</summary>
    public Reply Redeploy(string id) => RunThenStartRunner("redeploy", id);

    /// <summary>Tear down: destroying takes minutes, so it runs detached and the card follows the board.</summary>
    public Reply Destroy(string id)
    {
        launcher.StartDetached(["destroy", id]);
        return Reply.Of(CallResult.Done("tearing down the preview"));
    }

    /// <summary>Run canary: detached, and the banner follows canary.json. 409 while a live canary holds canary.lock.</summary>
    public Reply Canary()
    {
        if (ServerFiles.LockHolder(files, Path.Combine(paths.Home, "canary.lock")) is { } pid && procs.IsAlive(pid))
            return Reply.Error(409, "a canary is already running");
        // In the cloud the canary runs in a runner execution, never in the dashboard's container.
        if (runner.StartsExecutions)
        {
            var retry = factory.Run("canary", "-Retry");
            return retry.Ok ? Reply.Of(CallResult.Done($"{retry.Message.Trim()}; {runner.Start()}")) : Reply.Of(retry);
        }
        launcher.StartDetached(["canary", "-Force"]);
        return Reply.Of(CallResult.Done("canary started"));
    }

    public Reply QaLogin(string id) => Reply.Of(factory.Run("qa-login", id));

    public void StartRunner() => runner.Start();

    private Reply RunThenStartRunner(string command, string id)
    {
        var r = factory.Run(command, id);
        if (r.Ok) StartRunner();
        return Reply.Of(r);
    }
}
