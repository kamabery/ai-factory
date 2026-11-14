using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.TestHost;

/// <summary>
/// A stand-in for the slice of the GitHub REST API the factory uses, and HCP Terraform's under /api/v2, so targets and
/// deploys can be tested without a GitHub PAT or a network (it was factory/tests/fixtures/github-stub.ps1).
/// <para>
/// Repos are bare git repos at &lt;GitBase&gt;/&lt;owner&gt;/&lt;name&gt;.git, which is also what the factory clones
/// (FACTORY_GIT_BASE). Pull requests live in &lt;owner&gt;/&lt;name&gt;.prs.json beside them. Every request is appended to
/// &lt;GitBase&gt;/requests.jsonl as { method, path, auth, body }. The token's user is the login. Served:
/// GET /user · GET /user/repos · GET /repos/o/n · GET /repos/o/n/branches/&lt;b&gt; · POST /user/repos · POST /orgs/o/repos ·
/// GET|POST /repos/o/n/pulls · GET|PATCH /repos/o/n/pulls/N · PUT /repos/o/n/pulls/N/merge · DELETE /repos/o/n/git/refs/heads/&lt;b&gt;.
/// A merge is a real <c>git merge --no-ff</c> pushed to the PR's base: 405 on a conflict, 409 when <c>sha</c> is not the head.
/// GET / answers 200 (a liveness probe).
/// </para>
/// <para>
/// Deploys: POST /repos/o/n/actions/workflows/deploy.yml/dispatches · GET /repos/o/n/actions/runs[/&lt;id&gt;[/jobs]] ·
/// GET /repos/o/n/actions/jobs/&lt;id&gt;/logs · PUT /repos/o/n/environments/&lt;e&gt; · GET /repos/o/n/actions/oidc/customization/sub
/// (&lt;owner&gt;/&lt;name&gt;.oidc.json, else {use_default:true}) · POST|PATCH /repos/o/n/environments/&lt;e&gt;/variables[/&lt;name&gt;] ·
/// POST|PATCH /repos/o/n/actions/variables[/&lt;name&gt;]. A write under /actions or /environments answers 403 with
/// X-Accepted-GitHub-Permissions when &lt;owner&gt;/&lt;name&gt;.deny lists the permission it needs. A dispatched run completes at
/// once: <c>failure</c> when the branch head has deploy-fail.txt (its text is the error), else <c>success</c>, which writes
/// the workspace's outputs (&lt;owner&gt;/&lt;name&gt;.slow keeps it in_progress until the file goes). HCP: projects, varset
/// attachment, workspaces, outputs, runs (a destroy applies at once and clears the outputs; destroy-fails, destroy-hangs,
/// destroy-nothing and destroy-no-config under &lt;GitBase&gt; change that), plans and applies with a log-read-url under
/// /hcp-log/, safe-delete (safe-delete-busy holds how many answer 409), and a variable set's variables
/// (&lt;GitBase&gt;/varset-vars.json, else two fixed ones). A preview's urls are under /preview/&lt;o&gt;/&lt;n&gt;/&lt;env&gt;/: web/api/health
/// answers 200 Healthy, or 503 while &lt;owner&gt;/&lt;name&gt;.unhealthy exists; api/health answers 401 and web 200.
/// </para>
/// </summary>
public sealed partial class GitHubStubApp(string gitBase, string login, int port)
{
    public string GitBase { get; } = gitBase;

    public async Task<StubReply> Handle(HttpContext ctx)
    {
        Directory.CreateDirectory(GitBase);   // the simulator's -Reset may have deleted it
        var method = ctx.Request.Method;
        var path = ctx.Request.Path.Value ?? "/";
        var raw = await StubHost.BodyText(ctx);
        // GitHub's OAuth endpoint takes a form; everything else JSON.
        var body = raw.Length == 0 ? [] : ctx.Request.HasFormContentType ? Form(raw) : JsonNode.Parse(raw) as JsonObject ?? [];
        var log = new JsonObject { ["method"] = method, ["path"] = path, ["auth"] = ctx.Request.Headers.Authorization.ToString(), ["body"] = body.DeepClone() };
        File.AppendAllText(Path.Combine(GitBase, "requests.jsonl"), log.ToJsonString() + "\n");
        return Route(method, path, body, ctx.Request.Query, ctx.Request.Headers.Authorization.ToString());
    }

    private StubReply Route(string method, string path, JsonObject body, IQueryCollection q, string auth)
    {
        if (method == "GET" && path == "/") return new(200);
        if (method == "GET" && Preview().Match(path) is { Success: true } pv)
        {
            if (pv.Groups[3].Value == "web") return new(200, Text: "<!doctype html>");
            if (pv.Groups[3].Value == "api/health") return new(401);
            if (File.Exists(Path.Combine(GitBase, pv.Groups[1].Value, pv.Groups[2].Value + ".unhealthy"))) return new(503, Text: "Unhealthy");
            return new(200, Text: "Healthy");
        }
        if (path.StartsWith("/api/v2/", StringComparison.Ordinal)) return Hcp(method, path, body, q);
        if (method == "GET" && path.StartsWith("/hcp-log/", StringComparison.Ordinal))
        {
            var key = path["/hcp-log/".Length..];
            return new(200, Text: Str(State()["logs"]![key]) ?? (key.StartsWith("apply-", StringComparison.Ordinal) ? "Apply complete!" : ""));
        }
        if (App(method, path, body, auth) is { } app) return app;
        if (method == "GET" && path == "/user") return StubReply.Json(200, new JsonObject { ["login"] = login });
        if (method == "GET" && path == "/user/repos")
        {
            var dir = new DirectoryInfo(Path.Combine(GitBase, login));
            var repos = dir.Exists ? dir.GetDirectories("*.git").OrderByDescending(d => d.LastWriteTimeUtc).ToList() : [];
            return StubReply.Json(200, new JsonArray([.. repos.Select(d => (JsonNode)new JsonObject { ["full_name"] = $"{login}/{d.Name[..^4]}" })]));
        }
        if (method == "POST" && path == "/user/repos") return NewRepo(login, Str(body["name"]) ?? "", body);
        if (method == "POST" && OrgRepos().Match(path) is { Success: true } org) return NewRepo(org.Groups[1].Value, Str(body["name"]) ?? "", body);
        if (Repo().Match(path) is not { Success: true } m) return StubReply.Message(404, "Not Found");
        var (o, n, rest) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        var bare = Bare(o, n);
        if (!Directory.Exists(bare)) return StubReply.Message(404, "Not Found");
        if (method == "GET" && rest == "")
        {
            var meta = File.Exists(Path.Combine(GitBase, o, n + ".repo.json")) ? JsonNode.Parse(File.ReadAllText(Path.Combine(GitBase, o, n + ".repo.json"))) : null;
            return StubReply.Json(200, new JsonObject { ["full_name"] = $"{o}/{n}", ["private"] = meta?["private"]?.GetValue<bool>() == true, ["default_branch"] = "main" });
        }
        if (method == "GET" && rest.StartsWith("/branches/", StringComparison.Ordinal))
        {
            var b = rest["/branches/".Length..];
            var sha = Head(bare, b);
            if (sha.Length == 0) return StubReply.Message(404, "Branch not found");
            return StubReply.Json(200, new JsonObject { ["name"] = b, ["commit"] = new JsonObject { ["sha"] = sha } });
        }
        if (method == "GET" && rest == "/pulls")
        {
            var state = q["state"].ToString();
            var head = q["head"].ToString() is { Length: > 0 } h ? h.Split(':', 2)[^1] : "";
            var list = Prs(o, n).OfType<JsonObject>().Where(p => (state.Length == 0 || state == "all" || Str(p["state"]) == state) && (head.Length == 0 || Str(p["head"]) == head));
            return StubReply.Json(200, new JsonArray([.. list.Select(p => (JsonNode)new JsonObject
            {
                ["number"] = p["number"]?.DeepClone(), ["html_url"] = p["html_url"]?.DeepClone(), ["state"] = p["state"]?.DeepClone(),
                ["head"] = new JsonObject { ["ref"] = p["head"]?.DeepClone() }, ["base"] = new JsonObject { ["ref"] = p["base"]?.DeepClone() },
            })]));
        }
        if (method == "POST" && rest == "/pulls")
        {
            var head = Str(body["head"]) ?? "";
            if (Head(bare, head).Length == 0) return StubReply.Message(422, "Validation Failed: head");
            var prs = Prs(o, n);
            if (prs.OfType<JsonObject>().Any(p => Str(p["head"]) == head && Str(p["state"]) == "open")) return StubReply.Message(422, $"A pull request already exists for {o}:{head}.");
            var num = prs.Count + 1;
            var pr = new JsonObject
            {
                ["number"] = num, ["head"] = head, ["base"] = body["base"]?.DeepClone(), ["title"] = body["title"]?.DeepClone(), ["state"] = "open",
                ["merged"] = false, ["html_url"] = $"https://github.test/{o}/{n}/pull/{num}",
            };
            prs.Add(pr);
            SavePrs(o, n, prs);
            return StubReply.Json(201, new JsonObject { ["number"] = num, ["html_url"] = pr["html_url"]!.DeepClone(), ["state"] = "open" });
        }
        if (Pull().Match(rest) is { Success: true } pm)
        {
            var num = int.Parse(pm.Groups[1].Value);
            if (pm.Groups[2].Value == "/merge") return method == "PUT" ? Merge(o, n, num, body) : StubReply.Message(404, "Not Found");
            var prs = Prs(o, n);
            if (prs.OfType<JsonObject>().FirstOrDefault(p => p["number"]?.GetValue<int>() == num) is not { } pr) return StubReply.Message(404, "Not Found");
            if (method == "GET")
            {
                var head = pr["merged"]?.GetValue<bool>() == true ? Str(pr["head_sha"]) : Head(bare, Str(pr["head"]) ?? "");
                return StubReply.Json(200, new JsonObject
                {
                    ["number"] = num, ["html_url"] = pr["html_url"]?.DeepClone(), ["state"] = pr["state"]?.DeepClone(), ["merged"] = pr["merged"]?.GetValue<bool>() == true,
                    ["merge_commit_sha"] = pr["merge_commit_sha"]?.DeepClone(), ["head"] = new JsonObject { ["ref"] = pr["head"]?.DeepClone(), ["sha"] = head },
                    ["base"] = new JsonObject { ["ref"] = pr["base"]?.DeepClone() },
                });
            }
            if (method == "PATCH")
            {
                if (Str(body["state"]) is { Length: > 0 } st) pr["state"] = st;
                if (Str(body["base"]) is { Length: > 0 } nb)
                {
                    if (Head(bare, nb).Length == 0) return StubReply.Message(422, "Validation Failed: base");
                    pr["base"] = nb;
                }
                SavePrs(o, n, prs);
                return StubReply.Json(200, new JsonObject { ["number"] = num, ["state"] = pr["state"]?.DeepClone(), ["base"] = new JsonObject { ["ref"] = pr["base"]?.DeepClone() } });
            }
        }
        if (ActionsOrEnvironments().IsMatch(rest))
        {
            if (Denied(o, n, method, rest) is { } need)
                return new(403, new JsonObject { ["message"] = "Resource not accessible by personal access token" }, Headers: new Dictionary<string, string> { ["X-Accepted-GitHub-Permissions"] = need });
            return Actions(method, o, n, rest, body, q);
        }
        if (method == "DELETE" && rest.StartsWith("/git/refs/heads/", StringComparison.Ordinal))
        {
            var gone = rest["/git/refs/heads/".Length..];
            if (Git(bare, "branch", "--delete", "--force", gone).Code != 0) return StubReply.Message(422, "Reference does not exist");
            // As GitHub does: an open PR whose base branch is deleted is closed.
            var prs = Prs(o, n);
            var closed = prs.OfType<JsonObject>().Where(p => Str(p["state"]) == "open" && Str(p["base"]) == gone).ToList();
            foreach (var p in closed) p["state"] = "closed";
            if (closed.Count > 0) SavePrs(o, n, prs);
            return new(204);
        }
        return StubReply.Message(404, "Not Found");
    }

    // --- repos and pull requests --------------------------------------------------------------

    private string Bare(string o, string n) => Path.Combine(GitBase, o, n + ".git");

    private string Head(string bare, string branch) => Git(bare, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}") is { Code: 0 } r ? r.Out : "";

    private StubReply NewRepo(string o, string n, JsonObject body)
    {
        var bare = Bare(o, n);
        if (Directory.Exists(bare)) return StubReply.Message(422, "name already exists on this account");
        Directory.CreateDirectory(bare);
        Git(bare, "init", "-q", "--bare", "-b", "main");
        File.WriteAllText(Path.Combine(GitBase, o, n + ".repo.json"), body.ToJsonString());
        return StubReply.Json(201, new JsonObject { ["full_name"] = $"{o}/{n}", ["private"] = body["private"]?.GetValue<bool>() == true, ["default_branch"] = "main" });
    }

    private JsonArray Prs(string o, string n)
    {
        var f = Path.Combine(GitBase, o, n + ".prs.json");
        return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) switch { JsonArray a => a, JsonObject one => [one.DeepClone()], _ => [] } : [];
    }

    private void SavePrs(string o, string n, JsonArray prs) => File.WriteAllText(Path.Combine(GitBase, o, n + ".prs.json"), prs.ToJsonString());

    private StubReply Merge(string o, string n, int num, JsonObject body)
    {
        var prs = Prs(o, n);
        if (prs.OfType<JsonObject>().FirstOrDefault(p => p["number"]?.GetValue<int>() == num) is not { } pr || Str(pr["state"]) != "open")
            return StubReply.Message(404, "Not Found");
        var bare = Bare(o, n);
        var head = Head(bare, Str(pr["head"]) ?? "");
        if (Str(body["sha"]) is { Length: > 0 } want && want != head) return StubReply.Message(409, "Head branch was modified. Review and try the merge again.");
        var tmp = Path.Combine(Path.GetTempPath(), $"gh-stub-{Guid.NewGuid():N}");
        // Into the PR's base branch, as GitHub does: main, or the branch a stacked PR points at.
        var into = Str(pr["base"]) is { Length: > 0 } b ? b : "main";
        string sha;
        try
        {
            Git(GitBase, "clone", "-q", "--branch", into, bare, tmp);
            if (Git(tmp, "merge", "--no-ff", $"origin/{Str(pr["head"])}", "-m", Str(body["commit_title"]) ?? $"Merge pull request #{num}").Code != 0)
                return StubReply.Message(405, "Pull Request is not mergeable");
            if (Git(tmp, "push", "-q", "origin", $"HEAD:{into}") is { Code: not 0 } push) return StubReply.Message(500, push.Out);
            sha = Git(tmp, "rev-parse", "HEAD").Out;
        }
        finally { DeleteTree(tmp); }
        pr["state"] = "closed";
        pr["merged"] = true;
        pr["merge_commit_sha"] = sha;
        pr["head_sha"] = head;
        SavePrs(o, n, prs);
        return StubReply.Json(200, new JsonObject { ["sha"] = sha, ["merged"] = true, ["message"] = "Pull Request successfully merged" });
    }

    // --- Actions and environments -------------------------------------------------------------

    /// <summary>A fine-grained PAT that lacks a permission: &lt;owner&gt;/&lt;name&gt;.deny lists the ones it does not have.</summary>
    private string? Denied(string o, string n, string method, string rest)
    {
        var f = Path.Combine(GitBase, o, n + ".deny");
        if (method == "GET" || !File.Exists(f)) return null;
        var need = rest.StartsWith("/environments/", StringComparison.Ordinal) ? "environments=write"
            : rest.StartsWith("/actions/variables", StringComparison.Ordinal) ? "variables=write"
            : Dispatches().IsMatch(rest) ? "actions=write" : null;
        return need is not null && File.ReadAllLines(f).Select(l => l.Trim()).Contains(need) ? need : null;
    }

    private StubReply Actions(string method, string o, string n, string rest, JsonObject body, IQueryCollection q)
    {
        if (method == "POST" && rest == "/actions/workflows/deploy.yml/dispatches") return Dispatch(o, n, body);
        var s = State();
        var runs = s["runs"]!.AsObject();
        if (method == "GET" && rest == "/actions/runs")
        {
            var branch = q["branch"].ToString();
            var list = runs.Select(kv => kv.Value!.AsObject()).Where(r => Str(r["repo"]) == $"{o}/{n}" && (branch.Length == 0 || Str(r["head_branch"]) == branch))
                .OrderByDescending(r => r["id"]!.GetValue<int>()).ToList();
            return StubReply.Json(200, new JsonObject { ["total_count"] = list.Count, ["workflow_runs"] = new JsonArray([.. list.Select(r => r.DeepClone())]) });
        }
        if (method == "GET" && RunById().Match(rest) is { Success: true } rm)
        {
            if (runs[rm.Groups[1].Value] is not JsonObject r) return StubReply.Message(404, "Not Found");
            if (rm.Groups[2].Value == "/jobs")
                return StubReply.Json(200, new JsonObject { ["jobs"] = new JsonArray(new JsonObject { ["id"] = r["id"]!.GetValue<int>(), ["name"] = "deploy", ["conclusion"] = r["conclusion"]?.DeepClone() }) });
            if (Str(r["status"]) != "completed" && !File.Exists(Path.Combine(GitBase, Str(r["repo"]) + ".slow")))
            {
                r["status"] = "completed";
                r["conclusion"] = r["final"]?.DeepClone();
                Save(s);
            }
            return StubReply.Json(200, r.DeepClone());
        }
        if (method == "GET" && JobLogs().Match(rest) is { Success: true } jm)
            return new(200, Text: Str(s["logs"]![$"job-{jm.Groups[1].Value}"]) ?? "deploy succeeded");
        if (method == "GET" && rest == "/actions/oidc/customization/sub")
        {
            var f = Path.Combine(GitBase, o, n + ".oidc.json");
            return StubReply.Json(200, File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) : new JsonObject { ["use_default"] = true });
        }
        if (method == "PUT" && Environment().Match(rest) is { Success: true } em)
        {
            var vars = s["vars"]!.AsObject();
            vars[$"{o}/{n}/env/{em.Groups[1].Value}"] ??= new JsonObject();
            Save(s);
            return StubReply.Json(200, new JsonObject { ["name"] = em.Groups[1].Value });
        }
        if (Variables().Match(rest) is { Success: true } vm)
        {
            var scope = vm.Groups[2].Success ? $"{o}/{n}/env/{vm.Groups[2].Value}" : $"{o}/{n}/repo";
            var name = vm.Groups[4].Success ? vm.Groups[4].Value : "";
            var vars = s["vars"]!.AsObject();
            var set = vars[scope] as JsonObject ?? [];
            vars[scope] = set;
            if (method == "POST" && name.Length == 0)
            {
                var key = Str(body["name"]) ?? "";
                if (set.ContainsKey(key)) return StubReply.Message(409, "Already exists");
                set[key] = body["value"]?.DeepClone();
                Save(s);
                return StubReply.Json(201, new JsonObject());
            }
            if (method == "PATCH" && name.Length > 0)
            {
                set[name] = body["value"]?.DeepClone();
                Save(s);
                return new(204);
            }
        }
        return StubReply.Message(404, "Not Found");
    }

    private StubReply Dispatch(string o, string n, JsonObject body)
    {
        var bare = Bare(o, n);
        var branch = Str(body["ref"]) ?? "";
        var sha = Head(bare, branch);
        if (sha.Length == 0) return StubReply.Message(422, $"No ref found for: {branch}");
        // Like GitHub: an input the branch's deploy.yml does not declare is refused (environment is always declared;
        // the stub's branches carry no workflow file unless a test commits one).
        var wf = Git(bare, "show", $"{sha}:.github/workflows/deploy.yml").Out;
        var inputs = body["inputs"] as JsonObject ?? [];
        var extra = inputs.Select(kv => kv.Key).Where(k => k != "environment" && !Regex.IsMatch(wf, $@"(?m)^\s+{Regex.Escape(k)}:")).ToList();
        if (extra.Count > 0) return StubReply.Message(422, $"Unexpected inputs provided: [{string.Join(", ", extra)}]");
        var fail = Git(bare, "show", $"{sha}:deploy-fail.txt");
        var s = State();
        var id = NextId(s);
        var env = Str(inputs["environment"]) ?? "";
        var conclusion = fail.Code == 0 ? "failure" : "success";
        var run = new JsonObject
        {
            ["id"] = id, ["repo"] = $"{o}/{n}", ["head_branch"] = branch, ["head_sha"] = sha, ["event"] = "workflow_dispatch", ["status"] = "completed",
            ["conclusion"] = conclusion, ["environment"] = env, ["html_url"] = $"https://github.test/{o}/{n}/actions/runs/{id}", ["inputs"] = inputs.DeepClone(),
        };
        // <owner>/<name>.slow: the run stays in_progress until the file is gone (a real one takes about 60 minutes).
        if (File.Exists(Path.Combine(GitBase, o, n + ".slow"))) { run["status"] = "in_progress"; run["conclusion"] = null; run["final"] = conclusion; }
        s["runs"]![$"{id}"] = run;
        var ws = WorkspaceByName(s, $"{n}-{env}");
        var logs = s["logs"]!.AsObject();
        if (conclusion == "failure")
        {
            logs[$"job-{id}"] = $"deploy\nRun terraform apply\nError: {fail.Out}\n##[error]Process completed with exit code 1.";
            if (ws is not null)
            {
                var rid = $"run-{NextId(s)}";
                s["hcpRuns"]![rid] = new JsonObject { ["id"] = rid, ["ws"] = Str(ws["id"]), ["status"] = "errored", ["isDestroy"] = false };
                logs[$"plan-{rid}"] = $"Terraform v1.13.4\nError: {fail.Out}";
            }
        }
        else if (ws is not null)
        {
            var rid = $"run-{NextId(s)}";
            s["hcpRuns"]![rid] = new JsonObject { ["id"] = rid, ["ws"] = Str(ws["id"]), ["status"] = "applied", ["isDestroy"] = false };
            logs[$"plan-{rid}"] = "Plan: 21 to add, 0 to change, 0 to destroy.";
            // Every deploy smoke-checks its urls, so they point back at this stub (see /preview).
            var outputs = new JsonObject
            {
                ["api_url"] = Output($"http://localhost:{port}/preview/{o}/{n}/{env}/api", false),
                ["react_url"] = Output($"http://localhost:{port}/preview/{o}/{n}/{env}/web", false),
                ["qa_user"] = Output($"qa-{env}@{n}.test", false),
                ["qa_password"] = Output($"pw-{env}", true),
            };
            // <owner>/<name>.noauth: a target without the auth add-on, so no QA user to sign in as.
            if (File.Exists(Path.Combine(GitBase, o, n + ".noauth"))) { outputs.Remove("qa_user"); outputs.Remove("qa_password"); }
            s["outputs"]![Str(ws["id"])!] = outputs;
        }
        Save(s);
        return new(204);
    }

    // --- HCP Terraform ------------------------------------------------------------------------

    private StubReply Hcp(string method, string path, JsonObject body, IQueryCollection q)
    {
        if (method == "GET" && path == "/api/v2/account/details")
            return StubReply.Json(200, new JsonObject { ["data"] = new JsonObject { ["attributes"] = new JsonObject { ["username"] = "sim-hcp-user" } } });
        var s = State();
        var projects = s["projects"]!.AsObject();
        var workspaces = s["workspaces"]!.AsObject();
        var outputs = s["outputs"]!.AsObject();
        var hcpRuns = s["hcpRuns"]!.AsObject();
        Match m;
        if ((m = OrgProjects().Match(path)).Success)
        {
            if (method == "GET")
            {
                var want = q["filter[names]"].ToString();
                var list = projects.Select(kv => kv.Value!.AsObject()).Where(p => want.Length == 0 || Str(p["name"]) == want);
                return Data(200, new JsonArray([.. list.Select(p => (JsonNode)new JsonObject { ["id"] = p["id"]!.DeepClone(), ["type"] = "projects", ["attributes"] = new JsonObject { ["name"] = p["name"]!.DeepClone() } })]));
            }
            if (method == "POST")
            {
                var id = $"prj-{NextId(s)}";
                var name = Str(body["data"]?["attributes"]?["name"]);
                projects[id] = new JsonObject { ["id"] = id, ["name"] = name };
                Save(s);
                return Data(201, new JsonObject { ["id"] = id, ["type"] = "projects", ["attributes"] = new JsonObject { ["name"] = name } });
            }
        }
        if (method == "POST" && (m = VarsetProjects().Match(path)).Success)
        {
            var varsets = s["varsets"]!.AsObject();
            var list = varsets[m.Groups[1].Value] as JsonArray ?? [];
            varsets[m.Groups[1].Value] = list;
            var data = body["data"] switch { JsonArray a => a.OfType<JsonObject>(), JsonObject one => [one], _ => [] };
            foreach (var d in data)
                if (!list.Any(x => Str(x) == Str(d["id"]))) list.Add(Str(d["id"]));
            Save(s);
            return new(204);
        }
        if (method == "GET" && (m = OrgWorkspace().Match(path)).Success)
        {
            if (WorkspaceByName(s, m.Groups[2].Value) is not { } ws) return Errors(404, new JsonObject { ["status"] = "404", ["title"] = "not found" });
            return Data(200, new JsonObject { ["id"] = ws["id"]!.DeepClone(), ["type"] = "workspaces", ["attributes"] = new JsonObject { ["name"] = ws["name"]!.DeepClone(), ["auto-destroy-at"] = ws["autoDestroyAt"]?.DeepClone() } });
        }
        if (method == "POST" && OrgWorkspaces().IsMatch(path))
        {
            var a = body["data"]?["attributes"];
            var name = Str(a?["name"]) ?? "";
            if (WorkspaceByName(s, name) is not null) return Errors(422, new JsonObject { ["detail"] = "Name has already been taken" });
            var id = $"ws-{NextId(s)}";
            workspaces[id] = new JsonObject
            {
                ["id"] = id, ["name"] = name, ["project"] = Str(body["data"]?["relationships"]?["project"]?["data"]?["id"]),
                ["autoDestroyAt"] = a?["auto-destroy-at"]?.DeepClone(), ["deleted"] = false,
            };
            Save(s);
            return Data(201, new JsonObject { ["id"] = id, ["type"] = "workspaces", ["attributes"] = new JsonObject { ["name"] = name } });
        }
        if (method == "PATCH" && (m = WorkspaceById().Match(path)).Success)
        {
            if (workspaces[m.Groups[1].Value] is not JsonObject ws) return StubReply.Json(404, new JsonObject());
            if (body["data"]?["attributes"] is JsonObject attrs && attrs.ContainsKey("auto-destroy-at")) ws["autoDestroyAt"] = attrs["auto-destroy-at"]?.DeepClone();
            Save(s);
            return Data(200, new JsonObject { ["id"] = ws["id"]!.DeepClone() });
        }
        if (method == "GET" && (m = CurrentOutputs().Match(path)).Success)
        {
            var wsId = m.Groups[1].Value;
            var outs = outputs[wsId] as JsonObject ?? [];
            return Data(200, new JsonArray([.. outs.Select(kv =>
            {
                var sensitive = kv.Value!["sensitive"]?.GetValue<bool>() == true;
                return (JsonNode)new JsonObject
                {
                    ["id"] = $"wsout-{wsId}-{kv.Key}", ["type"] = "state-version-outputs",
                    ["attributes"] = new JsonObject { ["name"] = kv.Key, ["sensitive"] = sensitive, ["value"] = sensitive ? null : kv.Value["value"]?.DeepClone() },
                };
            })]));
        }
        if (method == "GET" && (m = OneOutput().Match(path)).Success)
        {
            if ((outputs[m.Groups[1].Value] as JsonObject)?[m.Groups[2].Value] is not JsonObject o) return StubReply.Json(404, new JsonObject());
            return Data(200, new JsonObject { ["attributes"] = new JsonObject { ["name"] = m.Groups[2].Value, ["sensitive"] = o["sensitive"]?.GetValue<bool>() == true, ["value"] = o["value"]?.DeepClone() } });
        }
        if (method == "GET" && (m = WorkspaceRuns().Match(path)).Success)
        {
            var wsId = m.Groups[1].Value;
            var runs = hcpRuns.Select(kv => kv.Value!.AsObject()).Where(r => Str(r["ws"]) == wsId).OrderByDescending(r => int.Parse(Digits().Replace(Str(r["id"])!, ""))).ToList();
            return Data(200, new JsonArray([.. runs.Select(HcpRun)]));
        }
        if (method == "POST" && path == "/api/v2/runs")
        {
            var wsId = Str(body["data"]?["relationships"]?["workspace"]?["data"]?["id"]) ?? "";
            if (workspaces[wsId] is null) return StubReply.Json(404, new JsonObject());
            var destroy = body["data"]?["attributes"]?["is-destroy"]?.GetValue<bool>() == true;
            // destroy-no-config: a workspace no apply ever reached (J-018).
            if (destroy && File.Exists(Path.Combine(GitBase, "destroy-no-config")))
                return Errors(422, new JsonObject { ["status"] = "422", ["title"] = "invalid", ["detail"] = "Configuration version is missing" });
            var rid = $"run-{NextId(s)}";
            var run = new JsonObject { ["id"] = rid, ["ws"] = wsId, ["status"] = "applied", ["isDestroy"] = destroy };
            hcpRuns[rid] = run;
            // destroy-fails: a destroy errors as Azure's late-added alert rule made canary-8's.
            if (destroy && File.Exists(Path.Combine(GitBase, "destroy-fails")))
            {
                run["status"] = "errored";
                s["logs"]![$"apply-{rid}"] = "Error: deleting Resource Group \"rg-x\": the Resource Group still contains Resources.";
            }
            // destroy-hangs: the run stays applying until the file goes, as a Container Apps environment's delete does.
            else if (destroy && File.Exists(Path.Combine(GitBase, "destroy-hangs"))) run["status"] = "applying";
            // destroy-nothing: the workspace is already empty, so HCP has nothing to apply (J-025).
            else if (destroy && File.Exists(Path.Combine(GitBase, "destroy-nothing"))) run["status"] = "planned_and_finished";
            else if (destroy) outputs.Remove(wsId);
            Save(s);
            return Data(201, HcpRun(run));
        }
        if (method == "GET" && (m = RunGet().Match(path)).Success)
        {
            if (hcpRuns[m.Groups[1].Value] is not JsonObject r) return StubReply.Json(404, new JsonObject());
            if (Str(r["status"]) == "applying" && !File.Exists(Path.Combine(GitBase, "destroy-hangs")))
            {
                r["status"] = "applied";
                outputs.Remove(Str(r["ws"])!);
                Save(s);
            }
            return Data(200, HcpRun(r));
        }
        if (method == "GET" && (m = PlanOrApply().Match(path)).Success)
            return Data(200, new JsonObject { ["attributes"] = new JsonObject { ["log-read-url"] = $"http://localhost:{port}/hcp-log/{m.Groups[2].Value}" } });
        if ((m = VarsetVars().Match(path)).Success)
        {
            var f = Path.Combine(GitBase, "varset-vars.json");
            var have = File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonArray ?? [] : null;
            // The variable set's variables: varset-vars.json, else two fixed ones.
            if (method == "GET")
            {
                var vars = have ?? [Var("TFC_AZURE_PROVIDER_AUTH", "true"), Var("TFC_AZURE_RUN_CLIENT_ID", "run-client")];
                return Data(200, new JsonArray([.. vars.OfType<JsonObject>().Select(v => (JsonNode)new JsonObject { ["id"] = $"var-{Str(v["key"])}", ["type"] = "vars", ["attributes"] = v.DeepClone() })]));
            }
            // A new variable, or a change to one, kept in varset-vars.json.
            if (method is "POST" or "PATCH")
            {
                var a = body["data"]?["attributes"] as JsonObject ?? [];
                var key = Str(a["key"]);
                var vars = new JsonArray([.. (have ?? []).OfType<JsonObject>().Where(v => Str(v["key"]) != key).Select(v => v.DeepClone()), new JsonObject
                {
                    ["key"] = key, ["value"] = a["value"]?.DeepClone(), ["category"] = a["category"]?.DeepClone(),
                    ["hcl"] = a["hcl"]?.GetValue<bool>() == true, ["sensitive"] = a["sensitive"]?.GetValue<bool>() == true,
                }]);
                File.WriteAllText(f, vars.ToJsonString());
                return Data(method == "POST" ? 201 : 200, new JsonObject { ["id"] = $"var-{key}", ["type"] = "vars", ["attributes"] = a.DeepClone() });
            }
        }
        if (method == "POST" && (m = SafeDelete().Match(path)).Success)
        {
            if (workspaces[m.Groups[1].Value] is not JsonObject ws) return StubReply.Json(404, new JsonObject());
            // safe-delete-busy holds how many more calls answer 409, as HCP does while it processes the state a destroy
            // just wrote (J-025).
            var busy = Path.Combine(GitBase, "safe-delete-busy");
            if (File.Exists(busy) && int.TryParse(File.ReadAllText(busy).Trim(), out var left) && left > 0)
            {
                File.WriteAllText(busy, $"{left - 1}");
                return Errors(409, new JsonObject { ["status"] = "409", ["title"] = "conflict", ["detail"] = "Latest workspace state is being processed to discover resources, please try again later" });
            }
            ws["deleted"] = true;
            Save(s);
            return new(204);
        }
        return Errors(404, new JsonObject { ["status"] = "404" });
    }

    private static JsonNode HcpRun(JsonObject r) => new JsonObject
    {
        ["id"] = r["id"]!.DeepClone(), ["type"] = "runs",
        ["attributes"] = new JsonObject { ["status"] = r["status"]!.DeepClone(), ["is-destroy"] = r["isDestroy"]?.GetValue<bool>() == true },
        ["relationships"] = new JsonObject
        {
            ["plan"] = new JsonObject { ["data"] = new JsonObject { ["id"] = $"plan-{Str(r["id"])}" } },
            ["apply"] = new JsonObject { ["data"] = new JsonObject { ["id"] = $"apply-{Str(r["id"])}" } },
        },
    };

    private static JsonObject Var(string key, string value) => new() { ["key"] = key, ["value"] = value, ["category"] = "env", ["sensitive"] = false };

    private static JsonObject Output(string value, bool sensitive) => new() { ["value"] = value, ["sensitive"] = sensitive };

    private static StubReply Data(int status, JsonNode data) => StubReply.Json(status, new JsonObject { ["data"] = data });

    private static StubReply Errors(int status, JsonObject error) => StubReply.Json(status, new JsonObject { ["errors"] = new JsonArray(error) });

    // --- deploy state: one JSON file for Actions and HCP together ------------------------------

    private string StateFile => Path.Combine(GitBase, "deploy-state.json");

    private JsonObject State()
    {
        var s = File.Exists(StateFile) ? JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject ?? [] : [];
        foreach (var k in new[] { "runs", "vars", "projects", "varsets", "workspaces", "hcpRuns", "outputs", "logs" })
            if (s[k] is not JsonObject) s[k] = new JsonObject();
        s["seq"] ??= 0;
        return s;
    }

    private void Save(JsonObject s) => File.WriteAllText(StateFile, s.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    private static int NextId(JsonObject s)
    {
        var next = s["seq"]!.GetValue<int>() + 1;
        s["seq"] = next;
        return next;
    }

    private static JsonObject? WorkspaceByName(JsonObject s, string name) =>
        s["workspaces"]!.AsObject().Select(kv => kv.Value as JsonObject).FirstOrDefault(w => w is not null && Str(w["name"]) == name && w["deleted"]?.GetValue<bool>() != true);

    // --- helpers ------------------------------------------------------------------------------

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    /// <summary><c>git -C dir</c> with a fixed identity; its exit code and its output, stdout then stderr, trimmed.</summary>
    private static (int Code, string Out) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in (string[])["-C", dir, "-c", "user.name=github", "-c", "user.email=github@stub", .. args]) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (output + err.Result).Trim());
    }

    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^/preview/([^/]+)/([^/]+)/[^/]+/(api/health|web|web/api/health)$")] private static partial Regex Preview();
    [GeneratedRegex(@"^/orgs/([^/]+)/repos$")] private static partial Regex OrgRepos();
    [GeneratedRegex(@"^/repos/([^/]+)/([^/]+)(/.*)?$")] private static partial Regex Repo();
    [GeneratedRegex(@"^/pulls/(\d+)(/merge)?$")] private static partial Regex Pull();
    [GeneratedRegex(@"^/(actions|environments)/")] private static partial Regex ActionsOrEnvironments();
    [GeneratedRegex(@"^/actions/workflows/.+/dispatches$")] private static partial Regex Dispatches();
    [GeneratedRegex(@"^/actions/runs/(\d+)(/jobs)?$")] private static partial Regex RunById();
    [GeneratedRegex(@"^/actions/jobs/(\d+)/logs$")] private static partial Regex JobLogs();
    [GeneratedRegex(@"^/environments/([^/]+)$")] private static partial Regex Environment();
    [GeneratedRegex(@"^/(environments/([^/]+)|actions)/variables(/([^/]+))?$")] private static partial Regex Variables();
    [GeneratedRegex(@"^/api/v2/organizations/([^/]+)/projects$")] private static partial Regex OrgProjects();
    [GeneratedRegex(@"^/api/v2/varsets/([^/]+)/relationships/projects$")] private static partial Regex VarsetProjects();
    [GeneratedRegex(@"^/api/v2/organizations/([^/]+)/workspaces/([^/]+)$")] private static partial Regex OrgWorkspace();
    [GeneratedRegex(@"^/api/v2/organizations/([^/]+)/workspaces$")] private static partial Regex OrgWorkspaces();
    [GeneratedRegex(@"^/api/v2/workspaces/([^/]+)$")] private static partial Regex WorkspaceById();
    [GeneratedRegex(@"^/api/v2/workspaces/([^/]+)/current-state-version-outputs$")] private static partial Regex CurrentOutputs();
    [GeneratedRegex(@"^/api/v2/state-version-outputs/wsout-(ws-\d+)-(.+)$")] private static partial Regex OneOutput();
    [GeneratedRegex(@"^/api/v2/workspaces/([^/]+)/runs$")] private static partial Regex WorkspaceRuns();
    [GeneratedRegex(@"^/api/v2/runs/([^/]+)$")] private static partial Regex RunGet();
    [GeneratedRegex(@"^/api/v2/(plans|applies)/((plan|apply)-(.+))$")] private static partial Regex PlanOrApply();
    [GeneratedRegex(@"^/api/v2/varsets/([^/]+)/relationships/vars")] private static partial Regex VarsetVars();
    [GeneratedRegex(@"^/api/v2/workspaces/([^/]+)/actions/safe-delete$")] private static partial Regex SafeDelete();
    [GeneratedRegex(@"\D")] private static partial Regex Digits();
    [GeneratedRegex(@"^/app-manifests/([^/]+)/conversions$")] private static partial Regex ManifestConversion();
    [GeneratedRegex(@"^/app/installations/(\d+)/access_tokens$")] private static partial Regex AccessTokens();

    // --- The GitHub App (spec 14) --------------------------------------------------------------------------------
    // The App lives in <GitBase>/app.json once a manifest code is converted (any code but "bad"), with a real RSA key, so
    // the factory's JWT is checked. Installations are <GitBase>/installations.json ([{ id, account, selection,
    // permissions }], written by a test or the simulator). An installation token is ghs_<id>_<n> and acts as its account;
    // like GitHub, it cannot read /user or create a user's repo. A user token is ghu_<n> (the code "sim-code", or a refresh
    // token ghr_<n>), lasting 8 hours.

    private string AppFile => Path.Combine(GitBase, "app.json");

    private StubReply? App(string method, string path, JsonObject body, string auth)
    {
        if (method == "POST" && ManifestConversion().Match(path) is { Success: true } mc) return Convert(mc.Groups[1].Value);
        if (method == "POST" && path == "/login/oauth/access_token") return OAuth(body);
        if (path == "/app" || path.StartsWith("/app/", StringComparison.Ordinal))
        {
            if (!JwtOk(auth)) return StubReply.Message(401, "A JSON web token could not be decoded");
            var app = JsonNode.Parse(File.ReadAllText(AppFile))!;
            if (method == "GET" && path == "/app") return StubReply.Json(200, new JsonObject { ["id"] = app["id"]!.GetValue<long>(), ["slug"] = Str(app["slug"]), ["name"] = Str(app["name"]) });
            if (method == "GET" && path == "/app/installations") return StubReply.Json(200, Installations());
            if (method == "POST" && AccessTokens().Match(path) is { Success: true } at)
            {
                var id = long.Parse(at.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (!Installations().OfType<JsonObject>().Any(i => i["id"]!.GetValue<long>() == id)) return StubReply.Message(404, "Not Found");
                var n = Counter("installation-tokens");
                return StubReply.Json(201, new JsonObject { ["token"] = $"ghs_{id}_{n}", ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ") });
            }
            return StubReply.Message(404, "Not Found");
        }
        var installation = InstallationOf(auth);
        if (installation is null) return null;
        if (path == "/user" || (method == "POST" && path == "/user/repos")) return StubReply.Message(403, "Resource not accessible by integration");
        if (method == "GET" && path == "/installation/repositories")
        {
            var account = Str(installation["account"])!;
            var dir = new DirectoryInfo(Path.Combine(GitBase, account));
            var repos = dir.Exists ? dir.GetDirectories("*.git").OrderByDescending(d => d.LastWriteTimeUtc).ToList() : [];
            return StubReply.Json(200, new JsonObject
            {
                ["total_count"] = repos.Count,
                ["repositories"] = new JsonArray([.. repos.Select(d => (JsonNode)new JsonObject { ["full_name"] = $"{account}/{d.Name[..^4]}", ["pushed_at"] = d.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") })]),
            });
        }
        return null;
    }

    private StubReply Convert(string code)
    {
        if (code == "bad") return StubReply.Message(404, "Not Found");
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var app = new JsonObject
        {
            ["id"] = 1001L, ["slug"] = "ai-factory-sim", ["name"] = "AI factory (sim)", ["client_id"] = "Iv1.sim", ["client_secret"] = "sim-secret",
            ["pem"] = rsa.ExportRSAPrivateKeyPem(), ["owner"] = new JsonObject { ["login"] = login },
        };
        File.WriteAllText(AppFile, app.ToJsonString());
        return StubReply.Json(201, app);
    }

    private StubReply OAuth(JsonObject form)
    {
        var refresh = Str(form["refresh_token"]);
        var ok = Str(form["grant_type"]) == "refresh_token" ? refresh?.StartsWith("ghr_", StringComparison.Ordinal) == true : Str(form["code"]) == "sim-code";
        if (!ok || Str(form["client_secret"]) != "sim-secret")
            return StubReply.Json(200, new JsonObject { ["error"] = "bad_verification_code", ["error_description"] = "The code passed is incorrect or expired." });
        var n = Counter("user-tokens");
        return StubReply.Json(200, new JsonObject { ["access_token"] = $"ghu_{n}", ["refresh_token"] = $"ghr_{n}", ["expires_in"] = 28800, ["token_type"] = "bearer" });
    }

    private JsonArray Installations()
    {
        var f = Path.Combine(GitBase, "installations.json");
        if (!File.Exists(f)) return [];
        return new JsonArray([.. JsonNode.Parse(File.ReadAllText(f))!.AsArray().OfType<JsonObject>().Select(i => (JsonNode)new JsonObject
        {
            ["id"] = i["id"]!.GetValue<long>(), ["account"] = new JsonObject { ["login"] = Str(i["account"]) },
            ["repository_selection"] = Str(i["selection"]) ?? "all", ["permissions"] = i["permissions"]?.DeepClone() ?? new JsonObject(),
        })]);
    }

    private JsonObject? InstallationOf(string auth)
    {
        if (!auth.StartsWith("Bearer ghs_", StringComparison.Ordinal)) return null;
        var id = auth["Bearer ghs_".Length..].Split('_')[0];
        var f = Path.Combine(GitBase, "installations.json");
        return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f))!.AsArray().OfType<JsonObject>().FirstOrDefault(i => i["id"]!.ToString() == id) : null;
    }

    private bool JwtOk(string auth)
    {
        if (!File.Exists(AppFile) || !auth.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var parts = auth["Bearer ".Length..].Split('.');
        if (parts.Length != 3) return false;
        var app = JsonNode.Parse(File.ReadAllText(AppFile))!;
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(Str(app["pem"]));
        static byte[] B64(string s) => System.Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64(parts[2]), System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1)) return false;
        var claims = JsonNode.Parse(B64(parts[1]))!;
        return Str(claims["iss"]) == app["id"]!.ToString() && claims["exp"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private int Counter(string name)
    {
        var f = Path.Combine(GitBase, $"{name}.count");
        var n = (File.Exists(f) ? int.Parse(File.ReadAllText(f), System.Globalization.CultureInfo.InvariantCulture) : 0) + 1;
        File.WriteAllText(f, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return n;
    }

    private static JsonObject Form(string raw)
    {
        var o = new JsonObject();
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            o[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return o;
    }
}
