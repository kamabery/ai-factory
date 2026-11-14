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
/// deploy.ps1 (contract, "Deploy"). GitHub is prepared first, so a missing GitHub PAT permission fails before anything
/// exists in HCP. Every wait is bounded (FACTORY_DEPLOY_TIMEOUT_MINUTES), and the board lock is released for all of it.
/// The live lessons kept here: a deploy whose caller's shell was cut off (600 s against a 25-minute apply) is resumed on
/// its run, not dispatched again (J-019, J-023); the sign-in smoke rides out Entra's replication of a new app (J-025);
/// a destroy waits out HCP's 409 while it processes the state (J-025) and deletes a workspace no apply reached (J-018).
/// </summary>
public sealed partial class DeployService(
    IGitHub github, IGitHubCredential credential, IHcpClient hcp, IToolRunner tools, IHttpProbe probe, IGitClient git, IFileStore files,
    IFactoryPaths paths, IDotEnv dotEnv, ISecrets secrets, IPause pause, TimeProvider clock, IOptions<DeployOptions> options, IOptions<FactoryOptions> factory)
    : IDeployService
{
    private DeployOptions O => options.Value;
    private static string Nl => Environment.NewLine;

    public DeployAttempt Attempt(IBoardSession session, Job job, bool resume, string? since)
    {
        var d = job.Node["deploy"]!.AsObject();
        var attempt = PsJsonReader.Int(d["attempts"]);
        var target = job.Target ?? "";
        var wt = paths.WorkDir(job.Id);
        var branch = $"factory/{job.Id}";
        var push = git.Run(wt, [.. github.GitAuth(), "push", "-q", "-f", "origin", $"{branch}:{branch}"]);
        if (push.Code != 0) return new DeployAttempt(false, null, null, null, null, null, $"## Push\nError: push failed: {push.Out}");
        var sha = git.Run(wt, "rev-parse", "HEAD").Out;

        DeployAttempt result = new(false, sha, null, null, null, null, "");
        session.Unlocked(() =>
        {
            string? wsId = null;
            try
            {
                // GitHub first: a missing PAT permission fails here, before anything exists in HCP.
                PrepareGitHub(target);
                var repo = Preview.RepoName(target);
                var project = hcp.EnsureProject(repo);
                foreach (var idle in new[] { "dev", "staging" }) hcp.EnsureWorkspace($"{repo}-{idle}", project);
                var destroyAt = clock.GetUtcNow().UtcDateTime.AddHours(24).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                wsId = hcp.EnsureWorkspace(PsJsonReader.Text(d["workspace"]), project, destroyAt);
                result = result with { DestroyAt = destroyAt, WorkspaceId = wsId };
                // since: the head the live preview was applied at, so a front-end-only change can skip Terraform. Only
                // when the branch's workflow declares it; GitHub refuses an input it does not.
                var declared = !string.IsNullOrEmpty(since) && Preview.DeclaresSince(files.ReadText(Path.Combine(wt, ".github", "workflows", "deploy.yml")));
                var run = DeployRun(target, PsJsonReader.Text(d["env"]), branch, sha, resume, declared ? since : null);
                result = result with { Run = PsJsonReader.Str(run["html_url"]) };
                if (!PsJsonReader.Is(PsJsonReader.Str(run["conclusion"]), "success"))
                {
                    result = result with { Log = $"## GitHub\n{RunLog(target, run)}\n\n## HCP\n{hcp.RunLog(wsId)}" };
                    return;
                }
                var outs = hcp.Outputs(wsId, ["qa_password"]);
                var urls = Preview.Urls(outs);
                result = result with { Urls = urls };
                // Step 7. J-026's /api/health was never probed: job deploys ran only the sign-in.
                var smoke = new List<string>();
                if (Smoke(urls, smoke) is { } err)
                {
                    result = result with { Log = $"## Smoke\nError: {err}\n{string.Join("\n", smoke)}" };
                    return;
                }
                var jobDir = paths.JobDir(job.Id);
                Write(Path.Combine(jobDir, $"smoke-{attempt}.log"), smoke);
                var signin = new List<string>();
                var shots = Path.Combine(jobDir, "signin");
                err = SigninSmoke(outs, urls, shots, signin);
                // A pass leaves evidence too (J-024's first live smoke left none).
                if (signin.Count > 0) Write(Path.Combine(shots, $"smoke-{attempt}.log"), signin);
                result = err is null ? result with { Ok = true } : result with { Log = $"## Sign-in smoke\nError: {err}\n{string.Join("\n", signin)}" };
            }
            catch (Exception e) when (Failures.Expected(e))
            {
                var log = "";
                if (wsId is not null)
                    try { log = $"\n\n## HCP\n{hcp.RunLog(wsId)}"; } catch (Exception inner) when (Failures.Expected(inner)) { }
                result = result with { Log = $"## Factory\nError: {e.Message}{log}" };
            }
        });
        return result;
    }

    public string? Destroy(IBoardSession session, Job job)
    {
        var d = job.Node["deploy"]!.AsObject();
        var name = PsJsonReader.Text(d["workspace"]);
        var known = PsJsonReader.Str(d["workspaceId"]);
        string? err = null;
        session.Unlocked(() =>
        {
            try
            {
                var wsId = !string.IsNullOrEmpty(known) ? known : hcp.WorkspaceId(name);
                err = string.IsNullOrEmpty(wsId) ? null : RemovePreview(wsId);
            }
            catch (Exception e) when (Failures.Expected(e)) { err = e.Message; }
        });
        return err;
    }

    public string? RemovePreview(string workspaceId)
    {
        string status;
        try { status = DestroyRun(workspaceId); }
        // No apply ever reached this workspace (J-018), so it has nothing to destroy.
        catch (FactoryException e) when (NoConfiguration().IsMatch(e.Message)) { status = "applied"; }
        // planned_and_finished: the workspace was already empty, so HCP had nothing to apply (J-025).
        if (!PsJsonReader.In(status, "applied", "planned_and_finished"))
        {
            // canary-8's said only "errored"; the reason, in HCP's own log, was a resource group Azure had added to.
            string? why;
            try { why = FirstError.Of(hcp.RunLog(workspaceId)); } catch (Exception e) when (Failures.Expected(e)) { why = null; }
            return $"destroy run ended {status}{(string.IsNullOrEmpty(why) ? "" : $": {why}")}";
        }
        RemoveWorkspace(workspaceId);
        return null;
    }

    public void PrepareGitHub(string target)
    {
        var clientId = dotEnv.Get("AZURE_GH_CLIENT_ID");
        var r = github.Send(HttpMethod.Put, $"/repos/{target}/environments/preview", new JsonObject());
        if (r.Status != 200) throw new FactoryException($"cannot create the preview environment: {GitHubErrors.Text(r)}");
        SetVariable($"/repos/{target}/environments/preview", "AZURE_CLIENT_ID", clientId);
        SetVariable($"/repos/{target}/environments/preview", "AZURE_TENANT_ID", dotEnv.Get("AZURE_TENANT_ID"));
        SetVariable($"/repos/{target}/environments/preview", "AZURE_SUBSCRIPTION_ID", dotEnv.Get("AZURE_SUBSCRIPTION_ID"));
        SetVariable($"/repos/{target}/actions", "TF_CLOUD_ORGANIZATION", dotEnv.Get("HCP_ORG"));

        // GitHub encrypts secrets with the repo's public key (a libsodium sealed box), which gh does for us. The GitHub PAT
        // reaches gh through its environment, the team token through stdin: neither is on a command line.
        var teamToken = secrets.Get(SecretNames.HcpTeamToken) ?? throw new FactoryException("HCP_TEAM_TOKEN is not set in .env");
        var s = tools.Gh(["secret", "set", "TF_API_TOKEN", "--env", "preview", "--repo", target], teamToken,
            new Dictionary<string, string?> { ["GH_TOKEN"] = credential.Token() });
        if (s.Code != 0)
            throw new FactoryException($"gh secret set failed: {s.Out}{(Http403().IsMatch(s.Out) ? $"; {GitHubErrors.PatNeeds("secrets=write")}" : "")}");

        // Repos on GitHub's immutable subject (the default for new ones) present repo:<owner>@<id>/<name>@<id>.
        var oidc = github.Send(HttpMethod.Get, $"/repos/{target}/actions/oidc/customization/sub");
        if (oidc.Status != 200) throw new FactoryException($"cannot read the OIDC subject format: {GitHubErrors.Text(oidc)}");
        var subject = Preview.OidcSubject(target, oidc.Body);
        // The app registration is in the home tenant, which is the tenant az is signed in to; nothing here reaches the
        // External tenant, whose Graph calls az rest would send to the home tenant instead.
        var list = tools.Az(["ad", "app", "federated-credential", "list", "--id", clientId ?? "", "--output", "json"]);
        if (list.Code != 0) throw new FactoryException($"az federated-credential list failed: {list.Out}");
        var existing = (JsonNode.Parse(list.Out) as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (existing.Any(c => PsJsonReader.Is(PsJsonReader.Str(c["subject"]), subject))) return;

        // Through a file (@path): az on Windows is a .cmd, and cmd strips the quotes of inline JSON.
        var name = Preview.CredentialName(target);
        var file = Path.Combine(factory.Value.TempPath, $"tmp{Guid.NewGuid():N}.tmp");
        files.WriteAtomic(file, Encoding.UTF8.GetBytes(new JsonObject
        {
            ["name"] = name, ["issuer"] = "https://token.actions.githubusercontent.com", ["subject"] = subject,
            ["audiences"] = new JsonArray("api://AzureADTokenExchange"),
        }.ToJsonString() + Nl));
        // A credential of this name with another subject (the repo's subject format changed) is updated in place.
        var verb = existing.Any(c => PsJsonReader.Is(PsJsonReader.Str(c["name"]), name)) ? "update" : "create";
        List<string> args = ["ad", "app", "federated-credential", verb, "--id", clientId ?? ""];
        if (verb == "update") args.AddRange(["--federated-credential-id", name]);
        args.AddRange(["--parameters", $"@{file}"]);
        ToolResult made;
        try { made = tools.Az(args); }
        finally { files.DeleteFile(file); }
        if (made.Code != 0) throw new FactoryException($"az federated-credential {verb} failed: {made.Out}");
    }

    public JsonObject DeployRun(string target, string environment, string branch, string? sha = null, bool resume = false, string? since = null)
    {
        var runsPath = $"/repos/{target}/actions/runs?branch={Uri.EscapeDataString(branch)}&event=workflow_dispatch";
        var runs = Runs(runsPath);
        // A run of this head that is still going (its deploy's process died, J-019) is waited on instead of dispatching
        // another; resumed, the newest one counts even if it has finished, after the process waiting on it died (J-023).
        var run = string.IsNullOrEmpty(sha) ? null : runs
            .Where(x => (resume || !PsJsonReader.Is(PsJsonReader.Str(x["status"]), "completed")) && PsJsonReader.Is(PsJsonReader.Str(x["head_sha"]), sha))
            .OrderByDescending(Id).FirstOrDefault();
        long last = 0;
        if (run is null)
        {
            last = runs.Count == 0 ? 0 : runs.Max(Id);
            var inputs = new JsonObject { ["environment"] = environment };
            if (!string.IsNullOrEmpty(since)) inputs["since"] = since;
            var r = github.Send(HttpMethod.Post, $"/repos/{target}/actions/workflows/deploy.yml/dispatches", new JsonObject { ["ref"] = branch, ["inputs"] = inputs });
            if (r.Status != 204) throw new FactoryException($"cannot dispatch deploy.yml: {GitHubErrors.Text(r)}");
        }
        var deadline = clock.GetUtcNow().AddMinutes(O.Timeout);
        while (true)
        {
            run = run is null
                ? Runs(runsPath).Where(x => Id(x) > last).OrderBy(Id).FirstOrDefault()
                : github.Send(HttpMethod.Get, $"/repos/{target}/actions/runs/{PsJsonReader.Text(run["id"])}").Body as JsonObject;
            if (run is not null && PsJsonReader.Is(PsJsonReader.Str(run["status"]), "completed")) return run;
            if (clock.GetUtcNow() > deadline)
                throw new FactoryException($"deploy.yml did not finish in time{(run is null ? "" : $": {PsJsonReader.Text(run["html_url"])}")}");
            pause.Sleep(TimeSpan.FromSeconds(O.Poll));
        }
    }

    public string RunLog(string target, JsonObject run)
    {
        var jobs = github.Send(HttpMethod.Get, $"/repos/{target}/actions/runs/{PsJsonReader.Text(run["id"])}/jobs").Body?["jobs"] as JsonArray ?? [];
        var text = new List<string> { $"run {PsJsonReader.Text(run["html_url"])}: {PsJsonReader.Text(run["conclusion"])}" };
        foreach (var j in jobs.OfType<JsonObject>().Where(j => !PsJsonReader.Is(PsJsonReader.Str(j["conclusion"]), "success")))
        {
            var log = github.Send(HttpMethod.Get, $"/repos/{target}/actions/jobs/{PsJsonReader.Text(j["id"])}/logs");
            text.Add($"--- job {PsJsonReader.Text(j["name"])}");
            text.Add(log.Body is JsonValue v && v.TryGetValue<string>(out var s) ? s : log.Body?.ToJsonString() ?? "");
        }
        return string.Join("\n", text);
    }

    public string? Smoke(JsonObject? urls, List<string> lines)
    {
        var health = PsJsonReader.Str(urls?["health"]);
        if (string.IsNullOrEmpty(health)) { lines.Add("no api_url output"); return "smoke: the workspace has no api_url output"; }
        var deadline = clock.GetUtcNow().AddSeconds(O.Smoke);
        string? first = null;
        List<string> targets = [health];
        if (PsJsonReader.Str(urls?["frontend"]) is { Length: > 0 } frontend) targets.Add(frontend);
        foreach (var u in targets)
        {
            ProbeResult r;
            long started;
            while (true)
            {
                started = clock.GetTimestamp();
                r = probe.Get(u, TimeSpan.FromSeconds(30));
                if (r.Status == 200 || clock.GetUtcNow() >= deadline) break;
                pause.Sleep(TimeSpan.FromSeconds(10));
            }
            var took = (int)Math.Round(clock.GetElapsedTime(started).TotalSeconds, MidpointRounding.ToEven);
            lines.Add($"{u} answered {r.Status} in {took}s, server: {r.Server} {r.Body[..Math.Min(200, r.Body.Length)]}");
            if (r.Status != 200 && first is null) first = $"smoke: {u} answered {r.Status}";
        }
        return first;
    }

    public string? SigninSmoke(IReadOnlyDictionary<string, string?> outputs, JsonObject? urls, string shots, List<string> lines)
    {
        var user = outputs.GetValueOrDefault("qa_user");
        var pw = outputs.GetValueOrDefault("qa_password");
        var frontend = PsJsonReader.Str(urls?["frontend"]);
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pw) || string.IsNullOrEmpty(frontend)) return null;
        files.EnsureDirectory(shots);
        string[] a = ["--url", frontend, "--user", user, "--shots", shots, "--seconds", O.Signin.ToString(CultureInfo.InvariantCulture)];
        for (var attempt = 1; ; attempt++)
        {
            var r = tools.Signin(a, pw);
            lines.AddRange(r.Lines.Select(l => l.Replace(pw, "***", StringComparison.Ordinal)));
            if (r.Code == 0) return null;
            var why = lines.LastOrDefault(l => SigninError().IsMatch(l));
            // Entra still replicating a new app's SPA client type (J-025: about 90 s).
            if (why is null || !Replicating().IsMatch(why) || attempt >= 5)
                return $"sign-in smoke: {(why is null ? $"exited {r.Code}" : SigninError().Replace(why, "", 1))}";
            lines.Add($"step: try {attempt} hit Entra replication; retrying in {O.SigninRetry} s");
            pause.Sleep(TimeSpan.FromSeconds(O.SigninRetry));
        }
    }

    /// <summary>Queues an auto-applied destroy run and waits for it; its final status, or <c>timed out</c>.</summary>
    private string DestroyRun(string workspaceId)
    {
        var deadline = clock.GetUtcNow().AddMinutes(O.Timeout);
        var run = hcp.QueueDestroy(workspaceId);
        while (!run.IsFinal)
        {
            if (clock.GetUtcNow() > deadline) return "timed out";
            pause.Sleep(TimeSpan.FromSeconds(O.Poll));
            run = hcp.Run(run.Id);
        }
        return run.Status;
    }

    /// <summary>Safe-deletes the workspace, waiting out the 409 HCP answers while it processes the state a destroy wrote (J-025).</summary>
    private void RemoveWorkspace(string workspaceId)
    {
        var deadline = clock.GetUtcNow().AddMinutes(5);
        while (true)
        {
            try { hcp.SafeDelete(workspaceId); return; }
            catch (FactoryException e) when (Conflict().IsMatch(e.Message) && clock.GetUtcNow() <= deadline) { }
            pause.Sleep(TimeSpan.FromSeconds(O.Poll));
        }
    }

    private void SetVariable(string scope, string name, string? value)
    {
        var body = new JsonObject { ["name"] = name, ["value"] = value };
        var r = github.Send(HttpMethod.Post, $"{scope}/variables", body);
        if (r.Status == 409) r = github.Send(HttpMethod.Patch, $"{scope}/variables/{name}", body.DeepClone());
        if (r.Status is not (201 or 204)) throw new FactoryException($"cannot set {name}: {GitHubErrors.Text(r)}");
    }

    private List<JsonObject> Runs(string path) =>
        (github.Send(HttpMethod.Get, path).Body?["workflow_runs"] as JsonArray ?? []).OfType<JsonObject>().ToList();

    private static long Id(JsonObject run) => long.TryParse(PsJsonReader.Text(run["id"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private void Write(string path, IEnumerable<string> lines)
    {
        files.EnsureDirectory(Path.GetDirectoryName(path)!);
        files.WriteAtomic(path, new UTF8Encoding(false).GetBytes(PsText.Content(lines, Nl)));
    }

    [GeneratedRegex("Configuration version is missing", RegexOptions.IgnoreCase)] private static partial Regex NoConfiguration();
    [GeneratedRegex("^HCP 409 ", RegexOptions.IgnoreCase)] private static partial Regex Conflict();
    [GeneratedRegex("HTTP 403", RegexOptions.IgnoreCase)] private static partial Regex Http403();
    [GeneratedRegex("^error: ", RegexOptions.IgnoreCase)] private static partial Regex SigninError();
    [GeneratedRegex("AADSTS9002326", RegexOptions.IgnoreCase)] private static partial Regex Replicating();
}
