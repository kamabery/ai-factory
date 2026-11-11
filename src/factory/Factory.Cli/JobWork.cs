using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <inheritdoc cref="IJobWork"/>
public sealed partial class JobWork(
    IFactoryPaths paths, IFileStore files, IGitClient git, IGitHub github, IGateShell shell, IFactoryLauncher launcher,
    IOptions<FactoryOptions> options, TimeProvider clock) : IJobWork
{
    private string Now => Iso.Format(clock.GetUtcNow());

    /// <summary>Test-Path &lt;wt&gt;/.git: a clone's .git folder (or a worktree's .git file).</summary>
    private bool IsClone(string wt) => files.DirectoryExists(Path.Combine(wt, ".git")) || files.Stat(Path.Combine(wt, ".git")) is not null;

    private GitResult Auth(string dir, params string[] args) => git.Run(dir, [.. github.GitAuth(), .. args]);

    public GitResult Fetch(string workDir, params string[] refs) => Auth(workDir, ["fetch", "-q", "origin", .. refs]);

    public string? EnsureClone(JsonObject board, string id)
    {
        var j = board.Job(id);
        if (SpecText.TargetError(j.Target) is { } err) return err;
        var target = j.Target!;
        var wt = paths.WorkDir(id);
        var branch = $"factory/{id}";
        if (!IsClone(wt))
        {
            if (EnsureRepo(target) is { } why) return why;
            TryDelete(wt);
            var parent = Path.GetDirectoryName(wt)!;
            files.EnsureDirectory(parent);
            var c = Auth(parent, "clone", "-q", github.CloneUrl(target), wt);
            if (c.Code != 0) return $"clone of {target} failed: {c.Out}";
        }
        var current = git.Run(wt, "branch", "--show-current").Out;
        if (PsJsonReader.Is(current, branch)) return null;
        if (git.Run(wt, "status", "--porcelain").Out.Length > 0) return $"work dir {wt} has uncommitted changes on '{current}'";
        if (MergeRules.BaseJob(board, j) is { } b && PsJsonReader.Is(b.Stage, Stages.Discarded)) return $"{id} is stacked on {b.Id}, which was discarded";
        var start = MergeRules.BaseBranch(board, j);
        GitResult r;
        if (git.Run(wt, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}").Code == 0) r = git.Run(wt, "checkout", "-q", "-B", branch, $"origin/{branch}");
        else if (git.Run(wt, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{start}").Code == 0) r = git.Run(wt, "checkout", "-q", "-b", branch, $"origin/{start}");
        else return $"{target} has no {start} branch";
        return r.Code == 0 ? null : $"checkout {branch} failed: {r.Out}";
    }

    /// <summary>Ensure-GitHubRepo: creates the target if it is missing, and pushes main with an empty first commit if it has none.</summary>
    private string? EnsureRepo(string target)
    {
        RepoState state;
        try { state = github.Lookup(target); }
        catch (FactoryException e) { return e.Message; }
        if (state == RepoState.Existing) return null;
        if (state == RepoState.Missing && github.CreateRepo(target) is { } err) return err;
        var seed = Path.Combine(paths.WorkRoot, "seed-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            files.EnsureDirectory(seed);
            var r = git.Run(seed, "init", "-q", "-b", "main");
            if (r.Code == 0) r = git.Run(seed, "-c", "user.name=factory", "-c", "user.email=factory@localhost", "commit", "-q", "--allow-empty", "-m", "Initial commit (factory)");
            if (r.Code == 0) r = Auth(seed, "push", "-q", github.CloneUrl(target), "main");
            return r.Code == 0 ? null : $"initial commit for {target} failed: {r.Out}";
        }
        finally { TryDelete(seed); }
    }

    private void TryDelete(string dir)
    {
        try { files.DeleteDirectory(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public (string? Url, string? Error) Publish(IBoardSession session, Job job)
    {
        var wt = paths.WorkDir(job.Id);
        var branch = $"factory/{job.Id}";
        if (!IsClone(wt)) return (null, $"{job.Id} has no work dir to publish");
        var r = Auth(wt, "push", "-q", "origin", $"{branch}:{branch}");
        if (r.Code != 0) return (null, $"push failed: {r.Out}");
        var pr = github.OpenPr(job.Target!, branch);
        if (pr is null)
        {
            var res = github.Send(HttpMethod.Post, $"/repos/{job.Target}/pulls", new JsonObject
            {
                ["title"] = $"{job.Id}: {PsJsonReader.Text(job.Node["title"])}", ["head"] = branch, ["base"] = MergeRules.BaseBranch(session.Board, job),
                ["body"] = $"Built by the factory from spec {job.Id}. Merged when a human moves it to Done.",
            });
            if (res.Status != 201) return (null, $"cannot open the PR: {GitHubErrors.Text(res)}");
            var body = res.Body as JsonObject;
            pr = new PullRequest(PsJsonReader.Int(body?["number"]), PsJsonReader.Text(body?["html_url"]));
            session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", job.Id), ("kind", "note"), ("detail", $"PR opened: {pr.Url}")));
        }
        job.Node["pr"] = new JsonObject { ["number"] = pr.Number, ["url"] = pr.Url };
        session.Save();
        return (pr.Url, null);
    }

    public IReadOnlyList<GateCommand> GateCommands(Job job)
    {
        if (options.Value.GateCmds is { Length: > 0 } json)
        {
            var node = JsonNode.Parse(json);
            IEnumerable<JsonNode?> items = node is JsonArray a ? a : [node];
            return [.. items.OfType<JsonObject>().Select(c => new GateCommand(PsJsonReader.Text(c["name"]), PsJsonReader.Text(c["cmd"])))];
        }
        return SpecText.Meta(PsText.Lines(files.ReadText(Path.Combine(paths.JobDir(job.Id), "spec.md")))).Gate;
    }

    public string? Gate(IBoardSession session, string id)
    {
        if (EnsureClone(session.Board, id) is { } err) return err;
        var j = session.Board.Job(id);
        var wt = paths.WorkDir(id);
        var cmds = GateCommands(j);
        if (cmds.Count == 0) return "no gate commands in the spec";
        var dir = paths.JobDir(id);
        var log = Path.Combine(dir, "gate.log");
        files.EnsureDirectory(dir);
        var nl = Environment.NewLine;
        files.WriteAtomic(log, Encoding.UTF8.GetBytes($"# gate {id} at {Now}{nl}"));
        var result = new JsonObject();
        session.Unlocked(() =>
        {
            foreach (var c in cmds)
            {
                // Add-Content: each write ends with a newline; the heading's own "`n" leads.
                files.AppendText(log, $"\n## {c.Name}: {c.Cmd}{nl}");
                var r = shell.Run(c.Cmd, wt);
                files.AppendText(log, r.Out + nl);
                files.AppendText(log, $"exit {r.Code}{nl}");
                result[c.Name] = r.Code;
            }
        });
        j = session.Board.Job(id);
        var sha = git.Run(wt, "rev-parse", "HEAD").Out;
        var detail = string.Join(' ', result.Select(kv => $"{kv.Key}={PsJsonReader.Text(kv.Value)}"));
        result["sha"] = sha;
        result["at"] = Now;
        j.Node["gate"] = result;
        session.Save();
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "gate"), ("detail", $"{detail} sha={sha[..Math.Min(7, sha.Length)]}")));
        return null;
    }

    public bool GateGreen(Job job)
    {
        if (!PsJsonReader.Truthy(job.Node["gate"])) return false;
        var head = git.Run(paths.WorkDir(job.Id), "rev-parse", "--verify", "--quiet", $"factory/{job.Id}").Out;
        return MergeRules.GateGreen(job, head);
    }

    public JsonObject? GitHubMerge(Job job)
    {
        if (job.Node["pr"] is not JsonObject pr) return null;
        return github.Pr(job.Target!, PsJsonReader.Int(pr["number"])) is { } p && PsJsonReader.Truthy(p["merged"]) ? p : null;
    }

    public IReadOnlyList<string> CompleteGitHubMerge(IBoardSession session, Job job, JsonObject pr)
    {
        var gated = PsJsonReader.Str(job.Node["gate"]?["sha"]);
        var head = PsJsonReader.Text(pr["head"]?["sha"]);
        var detail = $"PR #{PsJsonReader.Int(job.Node["pr"]?["number"])} merged on GitHub";
        if (!PsJsonReader.Is(head, gated))
            detail += $" at {head[..Math.Min(7, head.Length)]}, not the gated {(string.IsNullOrEmpty(gated) ? "head (no gate)" : gated[..Math.Min(7, gated.Length)])}";
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", job.Id), ("kind", "note"), ("detail", detail)));
        return CompleteMerge(session, job, PsJsonReader.Text(pr["merge_commit_sha"]));
    }

    public IReadOnlyList<string> CompleteMerge(IBoardSession session, Job job, string sha)
    {
        var board = session.Board;
        // GitHub closes a PR whose base branch is deleted: point the stacked jobs' PRs at main first.
        var stacked = board.Jobs()
            .Where(k => PsJsonReader.Is(k.Base, job.Id) && !PsJsonReader.In(k.Stage, Stages.Done, Stages.Discarded) && PsJsonReader.Truthy(k.Node["pr"])).ToList();
        foreach (var k in stacked)
        {
            var n = PsJsonReader.Int(k.Node["pr"]!["number"]);
            var res = github.Send(HttpMethod.Patch, $"/repos/{k.Target}/pulls/{n}", new JsonObject { ["base"] = "main" });
            var note = res.Status == 200 ? $"PR #{n} retargeted to main: {job.Id} merged" : $"PR #{n} not retargeted to main: {GitHubErrors.Text(res)}";
            session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", k.Id), ("kind", "note"), ("detail", note)));
        }
        github.Send(HttpMethod.Delete, $"/repos/{job.Target}/git/refs/heads/factory/{job.Id}");
        var wt = paths.WorkDir(job.Id);
        if (!IsClone(wt)) EnsureClone(board, job.Id);
        Fetch(wt, "main");
        var added = new JsonArray();
        var modified = new JsonArray();
        foreach (var line in NewLines().Split(git.Run(wt, "-c", "core.quotepath=false", "diff", "--name-status", "--no-renames", $"{sha}^1", sha).Out))
        {
            if (NameStatus().Match(line) is not { Success: true } m) continue;
            if (m.Groups[1].Value == "A") added.Add(m.Groups[2].Value);
            else if (m.Groups[1].Value == "M") modified.Add(m.Groups[2].Value);
        }
        job.Node["merged"] = new JsonObject
        {
            ["sha"] = sha, ["repo"] = job.Target, ["pr"] = PsJsonReader.Str(job.Node["pr"]?["url"]), ["added"] = added, ["modified"] = modified,
        };
        BoardMoves.Move(session, job, Stages.Done, "human", clock);
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", job.Id), ("kind", "merge"), ("detail", sha)));

        // Build check 2: the open siblings of the same idea were specced against the old main (J-022).
        var sibs = string.IsNullOrEmpty(job.Idea) ? [] : board.Jobs()
            .Where(k => !PsJsonReader.Is(k.Id, job.Id) && PsJsonReader.Is(k.Idea, job.Idea) && !PsJsonReader.In(k.Stage, Stages.Done, Stages.Discarded)).ToList();
        foreach (var k in sibs) k.Node["baseMoved"] = new JsonObject { ["by"] = job.Id, ["sha"] = sha, ["at"] = Now };
        if (sibs.Count > 0)
        {
            session.Save();
            foreach (var k in sibs) session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", k.Id), ("kind", "note"), ("detail", $"base moved: {job.Id} merged")));
        }
        WorkDirs.Remove(paths, files, session, job, clock);
        StartDestroy(job);

        // A merged bootstrap releases its idea for a /factory:idea session.
        var idea = job.IsBootstrap && !string.IsNullOrEmpty(job.Idea) ? board.Ideas().FirstOrDefault(i => PsJsonReader.Is(i.Id, job.Idea)) : null;
        if (idea is null || !PsJsonReader.Is(idea.Status, "waiting")) return [];
        idea.Node["status"] = "drafting";
        session.Save();
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("idea", idea.Id), ("kind", "idea"), ("detail", "drafting")));
        return [$"released {idea.Id}"];
    }

    /// <summary>deploy.ps1's Start-Destroy: a preview still up is destroyed by a detached <c>factory.ps1 destroy</c> (spec 09 ports it).</summary>
    private void StartDestroy(Job job)
    {
        var status = PsJsonReader.Str(job.Node["deploy"]?["status"]);
        if (status is null || PsJsonReader.In(status, "down", "destroying")) return;
        launcher.StartDetached(["destroy", job.Id]);
    }

    [GeneratedRegex(@"\r?\n")] private static partial Regex NewLines();
    [GeneratedRegex(@"^([A-Z])\t(.+)$")] private static partial Regex NameStatus();
}
