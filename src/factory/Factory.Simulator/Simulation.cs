using System.Text.Json.Nodes;
using Factory.TestHost;
using Microsoft.AspNetCore.Builder;

namespace Factory.Simulator;

/// <summary>
/// The simulated run (it was simulate.ps1): ideas, specs and jobs driven through the factory CLI's own commands into
/// every stage the dashboard shows, with no token spent. The targets are repos of the GitHub stub named in
/// FACTORY_ENV_FILE, or, with none, of a stub this run hosts over bare repos in &lt;state&gt;/github. Work dirs go to
/// &lt;state&gt;/work and the gate commands are injected (green). It never touches GitHub, this repo's git, dotnet or npm.
/// </summary>
public sealed class Simulation(string repoRoot, string stateDir, double speed, TextWriter log)
{
    private const string Sandbox = "sim/sandbox";
    private const string GreenGate = """[{"name":"build","cmd":"git --version"},{"name":"test","cmd":"git --version"},{"name":"ui","cmd":"git --version"}]""";

    private readonly SimCli _f = new(Environment.GetEnvironmentVariable("FACTORY_CLI") is { Length: > 0 } c ? c : Path.Combine(repoRoot, "artifacts", "bin", "Factory.Cli", "debug", "factory.dll"));
    private static readonly string TestHostDll = typeof(StubHost).Assembly.Location;

    public static string FindRepoRoot(string from)
    {
        for (var d = new DirectoryInfo(from); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "factory", "plugin", ".claude-plugin", "plugin.json"))) return d.FullName;
        throw new InvalidOperationException($"no factory checkout above {from}");
    }

    /// <summary>Deletes the state dir first, waiting up to 30 s for a live dashboard on it to let go ("The directory is not empty").</summary>
    public static void Reset(string stateDir, TimeSpan? wait = null)
    {
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(30));
        while (Directory.Exists(stateDir))
        {
            try
            {
                // git's objects are read-only, which a delete refuses on Windows.
                foreach (var f in Directory.EnumerateFiles(stateDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(stateDir, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) throw;
                Thread.Sleep(250);
            }
        }
    }

    public async Task Run()
    {
        if (!File.Exists(_f.Dll)) throw new InvalidOperationException($"{_f.Dll} is not built (dotnet build src/factory)");
        Directory.CreateDirectory(stateDir);
        _f.Env["FACTORY_HOME"] = stateDir;
        _f.Env["FACTORY_WORK"] = Environment.GetEnvironmentVariable("FACTORY_WORK") is { Length: > 0 } w ? w : Path.Combine(stateDir, "work");
        _f.Env["FACTORY_GATE_CMDS"] = Environment.GetEnvironmentVariable("FACTORY_GATE_CMDS") is { Length: > 0 } g ? g : GreenGate;
        // The App's token cache, never the machine's own <local app data>/ai-factory.
        _f.Env["FACTORY_LOCAL_DIR"] = Environment.GetEnvironmentVariable("FACTORY_LOCAL_DIR") is { Length: > 0 } l ? l : Path.Combine(stateDir, "local");

        WebApplication? stub = null;
        var envFile = Environment.GetEnvironmentVariable("FACTORY_ENV_FILE");
        if (string.IsNullOrEmpty(envFile))
        {
            var port = StubHost.FreePort();
            var gitBase = Path.Combine(stateDir, "github");
            stub = StubHost.Build(port, new GitHubStubApp(gitBase, "sim", port).Handle);
            await stub.StartAsync();
            envFile = Path.Combine(stateDir, "github.env");
            File.WriteAllLines(envFile, ["GITHUB_OWNER=sim", $"GITHUB_API_URL=http://localhost:{port}", $"FACTORY_GIT_BASE=file:///{gitBase.Replace('\\', '/')}"]);
        }
        _f.Env["FACTORY_ENV_FILE"] = envFile;
        // Signed in as the stub's GitHub App, the only GitHub credential, in whichever stub the .env points at (its own,
        // or the one Playwright started): seeded after any --reset, into FACTORY_SECRET_DIR, else <state>/secrets.
        var ghEnv = ReadEnv(envFile);
        if (ghEnv.TryGetValue("FACTORY_GIT_BASE", out var gitUrl) && gitUrl.StartsWith("file:///", StringComparison.Ordinal))
        {
            var secrets = Environment.GetEnvironmentVariable("FACTORY_SECRET_DIR") is { Length: > 0 } s ? s : Path.Combine(stateDir, "secrets");
            SimApp.Seed(gitUrl["file:///".Length..], ghEnv.GetValueOrDefault("GITHUB_OWNER") ?? "sim", secrets);
            _f.Env["FACTORY_SECRET_DIR"] = secrets;
        }
        try { await Scenario(envFile); }
        finally { if (stub is not null) await stub.DisposeAsync(); }
    }

    private void Tick(double units = 1) => Thread.Sleep((int)(600 * units / speed));

    private void Ev(string job, string kind, string agent) => _f.Must("event", "-Job", job, "-Kind", kind, "-Agent", agent);

    private static void Put(string path, string text) => File.WriteAllText(path, text + Environment.NewLine);

    private static void PutLines(string path, params string[] lines) => File.WriteAllLines(path, lines);

    private static Dictionary<string, string> ReadEnv(string path) =>
        File.ReadAllLines(path).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());

    private static string SpecText(string title, string intent) => $"""
        # Spec: {title}

        ## Intent
        {intent}

        ## Target
        - Repo: {Sandbox}

        ## Gate
        - build: `git --version`
        - test: `git --version`

        ## Acceptance criteria
        - [ ] AC1: {title} works as described — proven by `dotnet test`
        - [ ] AC2: the UI builds — proven by `npm run build`

        ## Scope
        - In: src/ui/react/src/routes
        - Out: the API

        ## Risks
        None beyond the usual.
        """;

    private JsonObject Job(string id) =>
        JsonNode.Parse(_f.Must("status", "-Json"))!["jobs"]!.AsArray().First(j => (string?)j!["id"] == id)!.AsObject();

    private int Round(string id) => (int)Job(id)["round"]!;

    private List<string> SimIdea(string text, params (string Title, string Intent)[] specs)
    {
        var idea = _f.Must("new-idea", text, "-Target", Sandbox);
        // The brief the /factory:idea session would have grilled out of the human.
        var unsure = specs.Length > 0 ? "Nothing." : "- which list, and what counts as fast?";
        Put(Path.Combine(stateDir, "ideas", idea, "brief.md"), $"# Brief: {text}\n\n## What it is for\n{text}\n\n## Not sure\n{unsure}");
        _f.Must("event", "-Idea", idea, "-Kind", "agent-start", "-Agent", "spec-writer");
        Tick(2);
        var ids = new List<string>();
        var k = 0;
        foreach (var (title, intent) in specs)
        {
            k++;
            var file = Path.Combine(stateDir, "ideas", idea, $"spec-{k}.md");
            Put(file, SpecText(title, intent));
            ids.Add(_f.Must("add-spec", idea, file));
            Tick(0.5);
        }
        _f.Must("event", "-Idea", idea, "-Kind", "agent-stop", "-Agent", "spec-writer");
        if (specs.Length > 0) _f.Must("idea-done", idea);
        else _f.Must("idea-done", idea, "-Failed", "-Reason", "rejected in session: too vague");
        return ids;
    }

    private void SimBuild(string id)
    {
        var round = Round(id);
        // The orchestrator's stream, and the builder's own transcript, as the Output tab reads them.
        var agentId = $"sim{new string([.. id.Where(char.IsDigit)])}r{round}";
        var transcript = Path.Combine(stateDir, "transcripts", $"agent-{agentId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        PutLines(Path.Combine(stateDir, "jobs", id, $"run-{round}.jsonl"),
            StreamLines.Init(), StreamLines.Say($"Starting {id}, round {round}."),
            StreamLines.Tool("t1", "Agent", new JsonObject { ["subagent_type"] = "factory:builder", ["description"] = $"Build {id}" }),
            StreamLines.Result("t1", "Built and committed."), StreamLines.Done(0.42, 95000));
        PutLines(transcript, StreamLines.Think(), StreamLines.Say("Writing the failing test first."),
            StreamLines.Tool("b1", "Bash", new JsonObject { ["command"] = "dotnet test" }), StreamLines.Result("b1", "Failed: 1"), StreamLines.Say("Now the implementation."));
        _f.Must("event", new JsonObject { ["job"] = id, ["kind"] = "agent-start", ["agent"] = "builder", ["agentId"] = agentId, ["transcript"] = transcript }.ToJsonString());
        var wt = _f.Must("checkout", id);
        File.AppendAllText(Path.Combine(wt, $"{id}.txt"), $"round {round}{Environment.NewLine}");
        _f.Git(wt, "add", "-A");
        _f.Git(wt, "-c", "user.email=sim@factory", "-c", "user.name=simulator", "commit", "-q", "-m", $"{id}: round {round}");
        var dir = Directory.CreateDirectory(Path.Combine(stateDir, "jobs", id, $"round-{round}")).FullName;
        // A sibling merged since this job was specced: the builder must say whether the spec still holds.
        var against = Job(id)["baseMoved"] is JsonObject moved ? $"\n\n## Spec against base\nSpec holds: yes\n{moved["by"]} touched none of this spec's paths." : "";
        Put(Path.Combine(dir, "build.md"), $"# Build: {id} — round {round}\n\nBranch: factory/{id}{against}\n\n## Evidence\n- simulated → exit 0");
        Tick(3);
        Ev(id, "agent-stop", "builder");
        _f.Must("set-stage", id, "review");
    }

    private void SimReview(string id, Dictionary<string, string>? verdicts = null)
    {
        var round = Round(id);
        var dir = Path.Combine(stateDir, "jobs", id, $"round-{round}");
        string[] reviewers = ["security", "code", "ux", "ui"];
        foreach (var r in reviewers) Ev(id, "agent-start", $"{r}-reviewer");
        foreach (var r in reviewers)
        {
            Tick();
            var v = verdicts?.GetValueOrDefault(r) ?? "PASS";
            var body = v == "CHANGES" ? "## Findings\n- src/ui/react/src/routes/books.tsx:42 uses a raw colour; use the token." : "## Findings\nNone.";
            Put(Path.Combine(dir, $"review-{r}.md"), $"Verdict: {v}\n\n# {r} review: {id} — round {round}\n\n{body}");
            _f.Must("set-review", id, r, v);
            Ev(id, "agent-stop", $"{r}-reviewer");
        }
        _f.F("read-reviews", id);
    }

    private void SimApprove(string id, string decision = "APPROVE", string reason = "N/A")
    {
        Ev(id, "agent-start", "approver");
        Tick(2);
        Put(Path.Combine(stateDir, "jobs", id, "decision.md"), $"Decision: {decision}\n\n# Decision: {id}\n\n## Basis\nEvery AC evidenced; every review PASS.\n\n## Escalation reason\n{reason}");
        Ev(id, "agent-stop", "approver");
        _f.Must("read-decision", id);
    }

    private void SimStart(string id)
    {
        Tick();
        _f.Must("set-stage", id, "build", "-By", "runner");
        _f.Must("set-cost", id, "0.42", "95000");
    }

    private async Task Scenario(string envFile)
    {
        var gh = ReadEnv(envFile);
        await SeedSandbox(gh);
        log.WriteLine($"simulating into {stateDir} (speed {speed})");

        var a = SimIdea("Let me find books quickly",
            ("Book search filter", "On /books, a text box filters the list by name as you type."),
            ("Sort books by name", "The /books list is sorted by name, ascending."),
            ("Page the book list", "The /books list shows 20 books per page."));
        var b = SimIdea("Log every request", ("Request logging", "Log every incoming request."));
        SimIdea("Make it faster");
        SimIdea("Unknown routes should not look broken", ("Not-found page", "An unknown route renders a not-found page."));

        // J-001: the happy path, all the way to Done.
        _f.Must("approve", a[0]);
        SimStart(a[0]); SimBuild(a[0]); SimReview(a[0]); SimApprove(a[0]);
        _f.F("gate", a[0]);
        Tick();
        _f.Must("merge", a[0]);

        // J-002: CHANGES twice → max-rounds; a human rework; CHANGES again → max-rounds.
        var ui = new Dictionary<string, string> { ["ui"] = "CHANGES" };
        _f.Must("approve", a[1]);
        SimStart(a[1]); SimBuild(a[1]); SimReview(a[1], ui);
        SimBuild(a[1]); SimReview(a[1], ui);
        Tick();
        _f.Must("rework", a[1], "use the colour tokens from docs/DESIGN.md");
        SimStart(a[1]); SimBuild(a[1]); SimReview(a[1], ui);

        // J-003: green, but its work dir has an uncommitted edit at merge → merge-blocked. Then "the human" cleans up.
        _f.Must("approve", a[2]);
        SimStart(a[2]); SimBuild(a[2]); SimReview(a[2]); SimApprove(a[2]);
        _f.F("gate", a[2]);
        _f.F("publish", a[2]);
        var wt = _f.Must("checkout", a[2]);
        File.AppendAllText(Path.Combine(wt, "README.md"), "uncommitted" + Environment.NewLine);
        _f.F("merge", a[2]);
        _f.Git(wt, "checkout", "--", "README.md");

        // J-004: discarded from the backlog. J-005 stays in the backlog for a human.
        Tick();
        _f.Must("discard", b[0]);

        // I-005 / J-006: a new target. The bootstrap job reaches Ready for review; its idea waits for the merge.
        var boot = _f.Must("new-idea", "A todo app, bootstrapped", "-Target", "sim/todo",
            "-Scaffold", """{"name":"todo","resource":"Todo","db":"sqlite","ui":"react","events":false}""").Split(' ');
        _f.Must("approve", boot[1]);
        SimStart(boot[1]); SimBuild(boot[1]); SimReview(boot[1]); SimApprove(boot[1]);
        _f.F("gate", boot[1]);
        _f.F("publish", boot[1]);

        // J-007: every review PASS, but the approver escalates: a manual AC has no evidence.
        var d = SimIdea("Sign in with Keycloak", ("Keycloak sign-in", "Users sign in through Keycloak."));
        _f.Must("approve", d[0]);
        SimStart(d[0]); SimBuild(d[0]); SimReview(d[0]);
        SimApprove(d[0], "ESCALATE", "AC8 (manual) has no evidence: is that acceptable before merge?\n\nOptions:\n1. **Verify first** under `aspire run`.\n2. Accept as is.");

        // J-008: deploys on for this one job (the rest keep them off). Every review PASS, the preview is applied through
        // the GitHub Actions and HCP stubs, the approver and the gate pass, and it waits in Ready for review with a live
        // preview: Launch, QA login and Tear down. Its devops report names a template gap, so it has lessons although it
        // went well.
        var p = SimIdea("Preview the reading list", ("Reading list preview", "The reading list is reviewed on a live preview."));
        _f.Must("approve", p[0]);
        var deployEnv = Path.Combine(stateDir, "deploy.env");
        File.WriteAllLines(deployEnv, File.ReadAllLines(envFile).Concat([
            "HCP_ORG=sim-org", "HCP_TOKEN=sim-hcp", "HCP_TEAM_TOKEN=sim-team", "HCP_AZURE_VARSET=sim-varset",
            "AZURE_GH_CLIENT_ID=sim-client", "AZURE_TENANT_ID=sim-tenant", "AZURE_SUBSCRIPTION_ID=sim-sub", $"HCP_API_URL={gh["GITHUB_API_URL"]}"]));
        var tool = new JsonArray("dotnet", TestHostDll, "tool", "--log", Path.Combine(stateDir, "tools.jsonl"), "--").ToJsonString();
        _f.Env["FACTORY_ENV_FILE"] = deployEnv;
        _f.Env["FACTORY_GH_CMD"] = tool;
        _f.Env["FACTORY_AZ_CMD"] = tool;
        _f.Env["FACTORY_SIGNIN_CMD"] = new JsonArray("dotnet", TestHostDll, "signin").ToJsonString();
        _f.Env["FACTORY_DEPLOY_POLL_SECONDS"] = "0";
        try
        {
            SimStart(p[0]); SimBuild(p[0]); SimReview(p[0]);
            Ev(p[0], "agent-start", "devops");
            _f.Must("deploy", p[0]);
            Put(Path.Combine(stateDir, "jobs", p[0], "round-1", "deploy.md"), $"""
                # Deploy: {p[0]}, round 1

                ## Attempts
                - Attempt 1: up

                ## Template gaps
                - Symptom: the React front end had no empty-state for an empty list
                  Cause: aspire-ui-react ships no empty-state component
                  Fix applied here: src/ui/react/src/components/ui/empty.tsx
                  Belongs in: template-projects:base-project/src/ui/react/src/components/ui
                """);
            Ev(p[0], "agent-stop", "devops");
            SimApprove(p[0]);
            _f.F("gate", p[0]);
            _f.F("publish", p[0]);
        }
        finally
        {
            _f.Env["FACTORY_ENV_FILE"] = envFile;
            foreach (var k in new[] { "FACTORY_GH_CMD", "FACTORY_AZ_CMD", "FACTORY_SIGNIN_CMD", "FACTORY_DEPLOY_POLL_SECONDS" }) _f.Env[k] = null;
        }

        // Lessons, as the runner compiles them when a pass leaves a job in Ready for review.
        foreach (var id in new[] { a[1], d[0], p[0] }) _f.F("lesson", id);

        // Evaluations of the merged J-001 and the max-rounds J-002, as a human session would write them.
        foreach (var (id, rating, verdict) in new[] { (a[0], "good", "Clean run: one round, every AC evidenced."), (a[1], "mixed", "The ui reviewer and the builder never converged.") })
        {
            var file = Path.Combine(stateDir, $"eval-{id}.md");
            Put(file, $"Rating: {rating}\n\n# Eval: {id}\n\n## Verdict\n{verdict}");
            _f.Must("eval", id, "-File", file);
            File.Delete(file);
        }

        log.WriteLine("simulation complete");
        log.WriteLine(_f.Must("status"));
    }

    /// <summary>The existing target, as a human would have it on GitHub: one commit on main.</summary>
    private async Task SeedSandbox(Dictionary<string, string> gh)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "ghu_sim");   // the App's user token, as a human creates the repo
        var api = gh["GITHUB_API_URL"].TrimEnd('/');
        if ((await http.GetAsync($"{api}/repos/{Sandbox}")).StatusCode != System.Net.HttpStatusCode.OK)
            (await http.PostAsync($"{api}/orgs/sim/repos", new StringContent("""{"name":"sandbox","private":true}""", System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        var seed = Path.Combine(stateDir, "seed");
        if (Directory.Exists(seed)) Reset(seed);
        _f.Git(null, "init", "-q", "-b", "main", seed);
        Put(Path.Combine(seed, "README.md"), "simulator sandbox");
        _f.Git(seed, "add", "-A");
        _f.Git(seed, "-c", "user.email=sim@factory", "-c", "user.name=simulator", "commit", "-q", "-m", "init");
        _f.Git(seed, "push", "-q", "--force", $"{gh["FACTORY_GIT_BASE"]}/{Sandbox}.git", "main");
        Reset(seed);
    }
}
