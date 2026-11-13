using System.Globalization;
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

/// <summary>
/// canary.ps1 (contract, "Canary"). The deploy path is tested against stubs, which encode our assumptions about GitHub,
/// Azure and HCP; the canary tests the real thing, through the same steps as <c>deploy</c> 3-7 with the dispatch on
/// <c>main</c>. A passing canary has proved the deploy, and a destroy takes as long again, so nothing waits for its
/// teardown; a teardown that fails does not fail the canary, and <c>sweep</c> retries it.
/// </summary>
public sealed partial class CanaryService(
    IDeployService deploy, IHcpClient hcp, ICanary canary, ICanaryLock canaryLock, IGitHub github, IGitClient git, IToolRunner tools,
    IFileStore files, IFactoryPaths paths, IDotEnv dotEnv, IFactoryLauncher launcher, IPause pause, TimeProvider clock,
    IOptions<DeployOptions> options) : ICanaryService
{
    private DeployOptions O => options.Value;
    private static string Nl => Environment.NewLine;
    private string CanaryPath => Path.Combine(paths.Home, "canary.json");
    private string Now => Iso.Format(clock.GetUtcNow());

    public string Run(IBoardSession session, bool force, bool full)
    {
        if (dotEnv.Get("HCP_ORG") is null) throw new FactoryException("canary refused: deploys are off (no HCP_ORG in .env)");
        var fp = canary.Fingerprint();
        var last = Read();
        if (!force && !full && last is not null && PsJsonReader.Is(PsJsonReader.Str(last["fingerprint"]), fp)
            && PsJsonReader.Is(PsJsonReader.Str(last["status"]), "pass"))
            return "canary: up to date";
        WaitTeardown(session);
        last = Read();
        string? err = null;
        JsonObject rec;
        using (Take("canary"))
        {
            var repo = canary.Repo();
            var sc = canary.Options();
            var n = 1 + (last is null ? 0 : PsJsonReader.Int(last["n"]));
            rec = new JsonObject
            {
                ["fingerprint"] = fp, ["status"] = "running", ["error"] = null, ["repo"] = repo, ["sha"] = null, ["urls"] = null,
                ["log"] = $"canary-{n}.log", ["startedAt"] = Now, ["finishedAt"] = null, ["n"] = n, ["full"] = full,
            };
            Save(rec);
            var log = new List<string>();
            // As canary.ps1's if ($err): an empty error is none.
            session.Unlocked(() => err = RunCanary(repo, sc, rec, log, full) is { Length: > 0 } e ? e : null);

            files.WriteAtomic(Path.Combine(paths.Home, PsJsonReader.Text(rec["log"])), new UTF8Encoding(false).GetBytes(string.Join("\n\n", log) + Nl));
            rec["status"] = err is null ? "pass" : "failed";
            rec["error"] = err;
            rec["finishedAt"] = Now;
            rec["teardown"] = new JsonObject { ["status"] = "destroying", ["error"] = null, ["startedAt"] = Now, ["failures"] = 0 };
            Save(rec);
            session.AddEvent(Events.Create(clock.GetUtcNow(), ("kind", "canary"), ("detail", err is null ? "pass" : $"failed: {err}")));
        }
        // Pass or fail, j000 comes down, and nothing waits for it: a destroy takes as long as the deploy.
        launcher.StartDetached(["canary-destroy"]);
        if (err is not null) throw new FactoryException($"canary failed: {err}; see {PsJsonReader.Text(rec["log"])}");
        return $"canary: pass ({PsJsonReader.Text(rec["urls"]?["api"])})";
    }

    public IReadOnlyList<string> Teardown(IBoardSession session)
    {
        var until = clock.GetUtcNow().AddSeconds(30);
        IDisposable? held;
        bool otherMachine;
        while ((held = canaryLock.TryTake(out otherMachine)) is null)
        {
            if (otherMachine) throw new FactoryException("canary-destroy refused: another canary is running on another machine (locks/canary)");
            if (clock.GetUtcNow() > until) throw new FactoryException("canary-destroy refused: another canary is running (canary.lock)");
            pause.Sleep(TimeSpan.FromSeconds(1));
        }
        using (held)
        {
            var rec = Read();
            if (rec is null) return [];
            if (rec["teardown"] is not JsonObject t)
                rec["teardown"] = t = new JsonObject { ["status"] = "destroying", ["error"] = null, ["startedAt"] = null, ["failures"] = 0 };
            t["status"] = "destroying";
            t["startedAt"] = Now;
            Save(rec);

            var name = Preview.RepoName(PsJsonReader.Text(rec["repo"]));
            string? down = null;
            session.Unlocked(() =>
            {
                try
                {
                    var wsId = hcp.WorkspaceId($"{name}-j000");
                    var gone = string.IsNullOrEmpty(wsId) ? null : deploy.RemovePreview(wsId);
                    down = !string.IsNullOrEmpty(gone) ? gone
                        : !string.IsNullOrEmpty(wsId) && !string.IsNullOrEmpty(hcp.WorkspaceId($"{name}-j000")) ? $"workspace {name}-j000 is still there" : null;
                }
                catch (Exception e) when (Failures.Expected(e)) { down = e.Message; }
            });

            rec = Read() ?? rec;
            t = rec["teardown"] as JsonObject ?? new JsonObject();
            t["status"] = down is null ? "down" : "failed";
            t["error"] = down;
            t["failures"] = down is null ? 0 : PsJsonReader.Int(t["failures"]) + 1;
            Save(rec);
            if (PsJsonReader.Str(rec["log"]) is { Length: > 0 } logName)
                files.AppendText(Path.Combine(paths.Home, logName),
                    $"\n\n## Destroy\n{(down is null ? $"workspace {name}-j000 destroyed and deleted" : $"Error: {down}")}{Nl}");
            session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", "canary"), ("kind", "destroy"), ("detail", down is null ? "down" : $"failed: {down}")));
            return [PsJsonReader.Text(t["status"])];
        }
    }

    /// <summary>Holds a new canary until the last one's teardown lets go of j000; one that started over 90 minutes ago has died.</summary>
    private void WaitTeardown(IBoardSession session)
    {
        while (true)
        {
            var t = Read()?["teardown"] as JsonObject;
            if (t is null || !PsJsonReader.Is(PsJsonReader.Str(t["status"]), "destroying")
                || !Sweep.DestroyRunning(PsJsonReader.Str(t["startedAt"]), clock.GetUtcNow(), O.DestroyStale)) return;
            // The teardown needs the board to record its result, so it is given up while waiting.
            session.Unlocked(() => pause.Sleep(TimeSpan.FromSeconds(Math.Max(1, O.Poll))));
        }
    }

    private IDisposable Take(string command) =>
        canaryLock.TryTake(out var otherMachine) ?? throw new FactoryException(otherMachine
            ? $"{command} refused: another canary is running on another machine (locks/canary)"
            : $"{command} refused: another canary is running (canary.lock)");

    /// <summary>The run, lock released: scaffold, deploy j000 from main, smoke. Null, or the first error; <paramref name="log"/> gets canary-&lt;n&gt;.log's sections.</summary>
    private string? RunCanary(string repo, JsonObject sc, JsonObject rec, List<string> log, bool full)
    {
        var dir = Path.Combine(paths.WorkRoot, "canary");
        var name = Preview.RepoName(repo);
        string? wsId = null;
        try
        {
            if (Publish(repo, sc, dir, rec, log, full) is { } failed) return failed;
            try
            {
                deploy.PrepareGitHub(repo);
                var project = hcp.EnsureProject(name);
                wsId = hcp.EnsureWorkspace($"{name}-j000", project,
                    clock.GetUtcNow().UtcDateTime.AddHours(24).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                var run = deploy.DeployRun(repo, "j000", "main");
                if (!PsJsonReader.Is(PsJsonReader.Str(run["conclusion"]), "success"))
                {
                    log.Add($"## GitHub\n{deploy.RunLog(repo, run)}\n\n## HCP\n{hcp.RunLog(wsId)}");
                    return FirstError.Of(string.Join("\n", log));
                }
                var outs = hcp.Outputs(wsId, ["qa_password"]);
                var urls = Preview.Urls(outs);
                rec["urls"] = urls;
                var smoke = new List<string>();
                var err = deploy.Smoke(urls, smoke);
                log.Add($"## Smoke\n{string.Join("\n", smoke)}");
                if (err is not null) return err;
                // Step 7a, when the canary scaffold has sign-in (a QA user on j000).
                var signin = new List<string>();
                err = deploy.SigninSmoke(outs, urls, Path.Combine(paths.Home, "canary-signin"), signin);
                if (signin.Count > 0) log.Add($"## Sign-in smoke\n{(err is null ? "" : $"Error: {err}\n")}{string.Join("\n", signin)}");
                return err;
            }
            catch (Exception e) when (Failures.Expected(e))
            {
                var hcpLog = "";
                if (wsId is not null)
                    try { hcpLog = $"\n\n## HCP\n{hcp.RunLog(wsId)}"; } catch (Exception inner) when (Failures.Expected(inner)) { }
                log.Add($"## Factory\nError: {e.Message}{hcpLog}");
                return FirstError.Of(string.Join("\n", log));
            }
        }
        finally { Remove(dir); }
    }

    /// <summary>Scaffolds into <paramref name="dir"/> and force-pushes it to the canary repo's main. Null, or why not.</summary>
    private string? Publish(string repo, JsonObject sc, string dir, JsonObject rec, List<string> log, bool full)
    {
        Remove(dir);
        files.EnsureDirectory(dir);
        var commands = Scaffold.Commands(sc);
        if (!string.IsNullOrEmpty(O.CanaryScaffoldCmd))
        {
            // The stub (tests) is handed the commands it stands in for, as arguments.
            var r = tools.Pwsh(dir, ["-File", O.CanaryScaffoldCmd, .. commands]);
            if (r.Code != 0) { log.Add($"## Scaffold\n{string.Join(' ', commands)}\n{r.Out}"); return $"scaffold: {commands[^1]} exited {r.Code}"; }
        }
        else
        {
            // Each is a program and its arguments, with no shell between: the options are validated, so none has a space.
            foreach (var c in commands)
            {
                var words = c.Split(' ');
                var r = tools.Run(dir, words[0], words[1..]);
                if (r.Code != 0) { log.Add($"## Scaffold\n{c}\n{r.Out}"); return $"scaffold: {c} exited {r.Code}"; }
            }
        }
        var tfvars = Path.Combine(dir, "IaC", "envs", "ephemeral.tfvars");
        // Sign-in needs the External ID ids in the scaffold's tfvars (a target commits them; canary 15).
        if (sc["entra"] is JsonObject entra)
        {
            if (files.ReadText(tfvars) is not { } text) return "scaffold: no IaC/envs/ephemeral.tfvars to put the Entra ids in";
            var w = entra.Select(kv => kv.Key.Length).DefaultIfEmpty(0).Max();
            var lines = PsText.Lines(text).Where(l => !EntraLine().IsMatch(l))
                .Concat(entra.Select(kv => $"{kv.Key.PadRight(w)} = \"{PsJsonReader.Text(kv.Value)}\""));
            files.WriteAtomic(tfvars, new UTF8Encoding(false).GetBytes(PsText.Content(lines, Nl)));
        }
        // The whole stack, off the shared platform: the only thing that proves a from-scratch create.
        if (full)
        {
            if (files.Stat(tfvars) is null) return "scaffold: no IaC/envs/ephemeral.tfvars to set use_platform in";
            files.AppendText(tfvars, "use_platform = false" + Nl);
        }
        git.Run(dir, "init", "-q", "-b", "main");
        git.Run(dir, "add", "-A");
        var commit = git.Run(dir, "-c", "user.name=factory", "-c", "user.email=factory@localhost", "commit", "-q", "-m", "canary scaffold");
        if (commit.Code != 0) return $"scaffold: commit failed: {commit.Out}";
        rec["sha"] = git.Run(dir, "rev-parse", "HEAD").Out;

        RepoState state;
        try { state = github.Lookup(repo); }
        catch (FactoryException e) { return $"github: {e.Message}"; }
        if (state == RepoState.Missing && github.CreateRepo(repo) is { Length: > 0 } made) return $"github: {made}";
        var push = git.Run(dir, [.. github.GitAuth(), "push", "-q", "-f", github.CloneUrl(repo), "HEAD:main"]);
        return push.Code == 0 ? null : $"github: push failed: {push.Out}";
    }

    private void Remove(string dir)
    {
        try { files.DeleteDirectory(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private JsonObject? Read() => files.ReadText(CanaryPath) is { } text ? JsonNode.Parse(text) as JsonObject : null;

    private void Save(JsonObject rec) => files.WriteAtomic(CanaryPath, Encoding.UTF8.GetBytes(PsJson.Write(rec, Nl)));

    [GeneratedRegex(@"^\s*entra_external_(tenant_id|subdomain|user_flow_id)\s*=", RegexOptions.IgnoreCase)] private static partial Regex EntraLine();
}
