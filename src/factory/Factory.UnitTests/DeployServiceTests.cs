using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

/// <summary>deploy.ps1's attempt, destroy, GitHub preparation, run polling and both smokes, ported to <see cref="DeployService"/>.</summary>
public class DeployServiceTests : CommandTestBase
{
    private readonly FakeActions _gh = new();
    private readonly IGitHubCredential _credential = Substitute.For<IGitHubCredential>();
    private readonly IHcpClient _hcp = Substitute.For<IHcpClient>();
    private readonly IToolRunner _tools = Substitute.For<IToolRunner>();
    private readonly IHttpProbe _probe = Substitute.For<IHttpProbe>();
    private readonly FakeGit _git = new FakeGit().On("rev-parse HEAD", 0, "sha-1");
    private readonly FakePause _pause;
    private readonly DeployOptions _o = new();
    private readonly Dictionary<string, string?> _outs = new()
    {
        ["react_url"] = "https://web", ["api_url"] = "https://api", ["qa_user"] = "qa@x.test", ["qa_password"] = "pw-1",
    };

    public DeployServiceTests()
    {
        _pause = new FakePause(Clock);
        _credential.Token().Returns("pat");
        foreach (var (k, v) in new[] { ("AZURE_GH_CLIENT_ID", "gh-app"), ("AZURE_TENANT_ID", "tenant"), ("AZURE_SUBSCRIPTION_ID", "sub"), ("HCP_ORG", "org"), ("HCP_TEAM_TOKEN", "team") })
            DotEnv.Get(k).Returns(v);
        _tools.Gh(default!, default, default).ReturnsForAnyArgs(new ToolResult(0, ""));
        _tools.Az(default!).ReturnsForAnyArgs(new ToolResult(0, "[]"));
        _tools.Signin(default!, default!).ReturnsForAnyArgs(new ToolResult(0, "step: signed in: Sign out is showing\nstep: /api/me answered 200"));
        _hcp.EnsureProject(Arg.Any<string>()).Returns("prj-1");
        _hcp.EnsureWorkspace(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(c => "ws-" + c.ArgAt<string>(0));
        _hcp.Outputs(default!, default!).ReturnsForAnyArgs(_outs);
        _hcp.RunLog(Arg.Any<string>()).Returns("run run-1: errored\nError: hcp says no");
        _hcp.QueueDestroy(Arg.Any<string>()).Returns(new HcpRun("run-9", "applied"));
        _probe.Get(default!, default).ReturnsForAnyArgs(new ProbeResult(200, "Healthy", "Kestrel"));
        _o.SigninRetrySeconds = "30";
    }

    private DeployService Sut => new(_gh.GitHub, _credential, _hcp, _tools, _probe, _git, Files, Paths, DotEnv, EnvSecrets.Over(DotEnv), _pause, Clock,
        Options.Create(_o), Options.Create(new FactoryOptions { TempPath = Path.Combine(Path.GetTempPath(), "deploy-tmp") }));

    private static (FakeSession Session, Job Job) InDeploy(int attempts = 1)
    {
        var s = FakeSession.With(Jobs.New("J-001", "deploy", target: "o/todo"));
        var j = s.Board.Job("J-001");
        Preview.Record(j)["attempts"] = attempts;
        j.Node["deploy"]!["status"] = "applying";
        return (s, j);
    }

    // --- the attempt ------------------------------------------------------------------------------

    [Fact]
    public void A_passing_attempt_pushes_prepares_applies_and_smoke_tests_then_leaves_its_evidence()
    {
        var (s, j) = InDeploy(attempts: 2);

        var r = Sut.Attempt(s, j, resume: false, since: null);

        Assert.True(r.Ok, r.Log);
        Assert.Equal(("sha-1", "https://gh/runs/100", "ws-todo-j001", "2026-11-11T12:00:00Z"), (r.Sha, r.Run, r.WorkspaceId, r.DestroyAt));
        Assert.Equal("""{"frontend":"https://web","api":"https://api","health":"https://web/api/health"}""", r.Urls!.ToJsonString());
        Assert.Equal(1, s.Unlocks);
        Assert.Equal((Path.Combine(Work, "jobs", "J-001"), "-c auth push -q -f origin factory/J-001:factory/J-001"), _git.Calls[0]);
        Received.InOrder(() =>
        {
            _hcp.EnsureProject("todo");
            _hcp.EnsureWorkspace("todo-dev", "prj-1");
            _hcp.EnsureWorkspace("todo-staging", "prj-1");
            _hcp.EnsureWorkspace("todo-j001", "prj-1", "2026-11-11T12:00:00Z");
        });
        Assert.Contains("https://web/api/health answered 200 in 0s, server: Kestrel Healthy", Files.Text(JobFile("J-001", "smoke-2.log")));
        var signin = Files.Text(JobFile("J-001", "signin/smoke-2.log"))!;
        Assert.Contains("Sign out is showing", signin);
        Assert.DoesNotContain("pw-1", signin);
        Assert.Equal("""{"environment":"j001"}""", _gh.Runs.Single()["inputs"]!.ToJsonString());
    }

    [Fact]
    public void A_failed_push_is_the_whole_log_and_nothing_else_runs()
    {
        var (s, j) = InDeploy();
        _git.On("push", 1, "rejected");

        var r = Sut.Attempt(s, j, false, null);

        Assert.Equal(new Cli.Abstractions.DeployAttempt(false, null, null, null, null, null, "## Push\nError: push failed: rejected"), r);
        Assert.Equal(0, s.Unlocks);
        Assert.Empty(_gh.Requests);
    }

    [Fact]
    public void A_failed_run_logs_GitHubs_failed_jobs_then_HCPs_run()
    {
        var (s, j) = InDeploy();
        _gh.Conclusion = "failure";

        var r = Sut.Attempt(s, j, false, null);

        Assert.False(r.Ok);
        Assert.Equal("## GitHub\nrun https://gh/runs/100: failure\n--- job deploy\nError: boom\n\n## HCP\nrun run-1: errored\nError: hcp says no", r.Log);
        _probe.DidNotReceiveWithAnyArgs().Get(default!, default);
    }

    [Fact]
    public void A_failed_smoke_skips_the_sign_in_and_ends_the_log_with_each_url()
    {
        var (s, j) = InDeploy();
        _o.SmokeSeconds = "0";
        _probe.Get("https://web/api/health", Arg.Any<TimeSpan>()).Returns(new ProbeResult(503, "Unhealthy", "envoy"));

        var r = Sut.Attempt(s, j, false, null);

        Assert.Equal("## Smoke\nError: smoke: https://web/api/health answered 503\nhttps://web/api/health answered 503 in 0s, server: envoy Unhealthy\nhttps://web answered 200 in 0s, server: Kestrel Healthy", r.Log);
        _tools.DidNotReceiveWithAnyArgs().Signin(default!, default!);
        Assert.Null(Files.Text(JobFile("J-001", "smoke-1.log")));
    }

    [Fact]
    public void A_failed_sign_in_ends_the_log_with_its_steps_never_the_password()
    {
        var (s, j) = InDeploy();
        _tools.Signin(default!, default!).ReturnsForAnyArgs(new ToolResult(1, "step: opened\nurl: https://login?pw-1\nerror: AADSTS500011: no api"));

        var r = Sut.Attempt(s, j, false, null);

        Assert.Equal("## Sign-in smoke\nError: sign-in smoke: AADSTS500011: no api\nstep: opened\nurl: https://login?***\nerror: AADSTS500011: no api", r.Log);
        Assert.NotNull(Files.Text(JobFile("J-001", "smoke-1.log")));
    }

    [Fact]
    public void A_step_that_throws_is_a_Factory_error_with_HCPs_log_once_there_is_a_workspace()
    {
        var (s, j) = InDeploy();
        _gh.Override = (m, p) => p.Contains("/dispatches", StringComparison.Ordinal) ? new GitHubResponse(422, new JsonObject { ["message"] = "No ref" }, null) : null;

        Assert.Equal("## Factory\nError: cannot dispatch deploy.yml: GitHub 422: No ref\n\n## HCP\nrun run-1: errored\nError: hcp says no", Sut.Attempt(s, j, false, null).Log);

        _hcp.ClearReceivedCalls();
        _gh.Override = (m, p) => p.EndsWith("/environments/preview", StringComparison.Ordinal) ? new GitHubResponse(403, new JsonObject { ["message"] = "denied" }, "environments=write") : null;
        var r = Sut.Attempt(s, j, false, null);
        Assert.Equal($"## Factory\nError: cannot create the preview environment: GitHub 403: denied; {GitHubErrors.PatNeeds("environments=write")}", r.Log);
        Assert.Null(r.WorkspaceId);
        _hcp.DidNotReceiveWithAnyArgs().EnsureProject(default!);
    }

    [Fact]
    public void An_HCP_log_that_cannot_be_read_leaves_only_the_error()
    {
        var (s, j) = InDeploy();
        _gh.Override = (m, p) => p.Contains("/dispatches", StringComparison.Ordinal) ? new GitHubResponse(0, JsonValue.Create("refused"), null) : null;
        _hcp.RunLog(Arg.Any<string>()).Throws(new FactoryException("HCP 500"));

        Assert.Equal("## Factory\nError: cannot dispatch deploy.yml: GitHub 0: refused", Sut.Attempt(s, j, false, null).Log);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Since_goes_out_only_when_the_branchs_deploy_yml_declares_it(bool declared, bool sent)
    {
        var (s, j) = InDeploy();
        Files.Put(Path.Combine(Work, "jobs", "J-001", ".github", "workflows", "deploy.yml"),
            "on:\n  workflow_dispatch:\n    inputs:\n      environment:\n" + (declared ? "      since:\n" : ""));

        Sut.Attempt(s, j, false, "sha-0");

        Assert.Equal(sent, _gh.Runs.Single()["inputs"]!.AsObject().ContainsKey("since"));
    }

    // --- the run ----------------------------------------------------------------------------------

    [Fact]
    public void DeployRun_dispatches_then_polls_the_new_run_until_it_completes()
    {
        _gh.AddRun(5, "old", "completed", "success");
        _gh.Looks = 2;
        _o.PollSeconds = "15";

        var run = Sut.DeployRun("o/todo", "j001", "factory/J-001", "sha-1");

        Assert.Equal((6L, "completed"), ((long)run["id"]!, (string?)run["status"]));
        Assert.Equal("""{"ref":"factory/J-001","inputs":{"environment":"j001"}}""", _gh.To("/dispatches").Single().Body!.ToJsonString());
        Assert.Equal("/repos/o/todo/actions/runs?branch=factory%2FJ-001&event=workflow_dispatch", _gh.Requests[0].Path);
        Assert.All(_pause.Sleeps, t => Assert.Equal(TimeSpan.FromSeconds(15), t));
    }

    [Fact]
    public void DeployRun_waits_on_a_run_of_the_head_that_is_still_going_and_dispatches_none()
    {
        // J-019: each retry of a deploy whose process died dispatched another run and spent an attempt.
        var going = _gh.AddRun(7, "sha-1", "in_progress");
        _gh.AddRun(6, "sha-1", "completed", "failure");
        _pause.Meanwhile = () => { going["status"] = "completed"; going["conclusion"] = "success"; };

        var run = Sut.DeployRun("o/todo", "j001", "factory/J-001", "sha-1");

        Assert.Equal(7L, (long)run["id"]!);
        Assert.Empty(_gh.To("/dispatches"));
    }

    [Fact]
    public void A_resumed_DeployRun_takes_the_newest_run_of_the_head_even_when_it_has_finished()
    {
        // J-023: the run succeeded 7 minutes after the shell waiting on it died; resuming dispatched a second, needless one.
        _gh.AddRun(8, "sha-1", "completed", "success");
        _gh.AddRun(9, "sha-1", "completed", "failure");
        _gh.AddRun(10, "sha-2", "completed", "success");

        Assert.Equal(9L, (long)Sut.DeployRun("o/todo", "j001", "factory/J-001", "sha-1", resume: true)["id"]!);
        Assert.Empty(_gh.To("/dispatches"));
        Assert.Equal(10L + 1, (long)Sut.DeployRun("o/todo", "j001", "factory/J-001", "sha-1")["id"]!);
    }

    [Fact]
    public void DeployRun_gives_up_at_the_timeout_naming_the_run_when_there_is_one()
    {
        _o.TimeoutMinutes = "1";
        _gh.Looks = 1000;
        Assert.Equal("deploy.yml did not finish in time: https://gh/runs/100",
            Assert.Throws<FactoryException>(() => Sut.DeployRun("o/todo", "j001", "factory/J-001")).Message);

        _gh.Override = (m, p) => p.Contains("/actions/runs?", StringComparison.Ordinal) ? new GitHubResponse(200, new JsonObject(), null)
            : p.Contains("/dispatches", StringComparison.Ordinal) ? new GitHubResponse(204, null, null) : null;
        Assert.Equal("deploy.yml did not finish in time", Assert.Throws<FactoryException>(() => Sut.DeployRun("o/todo", "j001", "main")).Message);
    }

    [Fact]
    public void RunLog_has_the_failed_jobs_logs_only()
    {
        Assert.Equal("run https://gh/runs/3: failure\n--- job deploy\nError: boom", Sut.RunLog("o/todo", _gh.AddRun(3, "s", "completed", "failure")));

        _gh.Override = (m, p) => p.EndsWith("/logs", StringComparison.Ordinal) ? new GitHubResponse(200, new JsonObject { ["x"] = 1 }, null) : null;
        Assert.EndsWith("--- job deploy\n{\"x\":1}", Sut.RunLog("o/todo", _gh.AddRun(4, "s", "completed", "failure")));
    }

    // --- GitHub and Azure preparation -------------------------------------------------------------

    [Fact]
    public void PrepareGitHub_sets_the_preview_variables_the_secret_through_gh_and_creates_the_federated_credential()
    {
        _gh.Override = (m, p) => m == "POST" && p.EndsWith("/actions/variables", StringComparison.Ordinal) ? new GitHubResponse(409, null, null) : null;

        Sut.PrepareGitHub("o/todo");

        Assert.Equal(["AZURE_CLIENT_ID", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "TF_CLOUD_ORGANIZATION"],
            _gh.To("/variables").Where(r => r.Method == "POST").Select(r => (string?)r.Body!["name"]));
        var patch = _gh.Requests.Single(r => r.Method == "PATCH");
        Assert.Equal(("/repos/o/todo/actions/variables/TF_CLOUD_ORGANIZATION", "org"), (patch.Path, (string?)patch.Body!["value"]));
        _tools.Received(1).Gh(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "secret", "set", "TF_API_TOKEN", "--env", "preview", "--repo", "o/todo" })),
            "team", Arg.Is<IReadOnlyDictionary<string, string?>>(e => e["GH_TOKEN"] == "pat"));
        _tools.Received(1).Az(Arg.Is<IReadOnlyList<string>>(a => string.Join(' ', a).StartsWith("ad app federated-credential create --id gh-app --parameters @", StringComparison.Ordinal)));
        Assert.DoesNotContain(Files.Writes, w => Files.Text(w) is not null);
    }

    [Fact]
    public void The_credential_file_holds_the_repos_immutable_subject()
    {
        _gh.Oidc = JsonNode.Parse("""{"use_immutable_subject":true,"sub_claim_prefix":"repo:o@1/todo@2"}""")!;
        string? written = null;
        _tools.Az(default!).ReturnsForAnyArgs(c =>
        {
            var a = c.Arg<IReadOnlyList<string>>();
            if (a[3] == "list") return new ToolResult(0, "[]");
            written = Files.Text(a[^1][1..]);
            return new ToolResult(0, "{}");
        });

        Sut.PrepareGitHub("o/todo");

        var cred = JsonNode.Parse(written!)!;
        Assert.Equal(("preview-o-todo", "https://token.actions.githubusercontent.com", "repo:o@1/todo@2:environment:preview", "api://AzureADTokenExchange"),
            ((string?)cred["name"], (string?)cred["issuer"], (string?)cred["subject"], (string?)cred["audiences"]![0]));
    }

    [Fact]
    public void A_credential_of_that_name_with_a_stale_subject_is_updated_and_one_with_the_subject_is_left_alone()
    {
        _tools.Az(Arg.Is<IReadOnlyList<string>>(a => a[3] == "list"))
            .Returns(new ToolResult(0, """[{"name":"preview-o-todo","subject":"repo:o/old:environment:preview"}]"""));
        Sut.PrepareGitHub("o/todo");
        _tools.Received(1).Az(Arg.Is<IReadOnlyList<string>>(a => string.Join(' ', a).StartsWith("ad app federated-credential update --id gh-app --federated-credential-id preview-o-todo --parameters @", StringComparison.Ordinal)));

        _tools.ClearReceivedCalls();
        _tools.Az(Arg.Is<IReadOnlyList<string>>(a => a[3] == "list"))
            .Returns(new ToolResult(0, """[{"name":"other","subject":"repo:o/todo:environment:preview"}]"""));
        Sut.PrepareGitHub("o/todo");
        _tools.Received(1).Az(Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public void PrepareGitHub_refuses_each_failure_on_the_way()
    {
        string Refusal() => Assert.Throws<FactoryException>(() => Sut.PrepareGitHub("o/todo")).Message;

        _gh.Override = (m, p) => p.EndsWith("/environments/preview/variables", StringComparison.Ordinal) ? new GitHubResponse(403, new JsonObject { ["message"] = "no" }, "variables=write") : null;
        Assert.Equal($"cannot set AZURE_CLIENT_ID: GitHub 403: no; {GitHubErrors.PatNeeds("variables=write")}", Refusal());

        _gh.Override = (m, p) => p.EndsWith("/oidc/customization/sub", StringComparison.Ordinal) ? new GitHubResponse(500, new JsonObject { ["message"] = "x" }, null) : null;
        Assert.Equal("cannot read the OIDC subject format: GitHub 500: x", Refusal());

        _gh.Override = null;
        _tools.Gh(default!, default, default).ReturnsForAnyArgs(new ToolResult(1, "failed to set secret: HTTP 403: Resource not accessible"));
        Assert.Equal($"gh secret set failed: failed to set secret: HTTP 403: Resource not accessible; {GitHubErrors.PatNeeds("secrets=write")}", Refusal());
        _tools.Gh(default!, default, default).ReturnsForAnyArgs(new ToolResult(1, "network down"));
        Assert.Equal("gh secret set failed: network down", Refusal());

        _tools.Gh(default!, default, default).ReturnsForAnyArgs(new ToolResult(0, ""));
        _tools.Az(Arg.Is<IReadOnlyList<string>>(a => a[3] == "list")).Returns(new ToolResult(1, "az login first"));
        Assert.Equal("az federated-credential list failed: az login first", Refusal());

        _tools.Az(Arg.Is<IReadOnlyList<string>>(a => a[3] == "list")).Returns(new ToolResult(0, "[]"));
        _tools.Az(Arg.Is<IReadOnlyList<string>>(a => a[3] == "create")).Returns(new ToolResult(1, "Insufficient privileges"));
        Assert.Equal("az federated-credential create failed: Insufficient privileges", Refusal());
        Assert.DoesNotContain(Files.Writes, w => Files.Text(w) is not null);

        DotEnv.Get("HCP_TEAM_TOKEN").Returns((string?)null);
        Assert.Equal("HCP_TEAM_TOKEN is not set in .env", Refusal());
    }

    // --- smokes -----------------------------------------------------------------------------------

    [Fact]
    public void Smoke_retries_each_url_every_10_seconds_until_it_answers_200()
    {
        var answers = new Queue<int>([500, 500, 200, 200]);
        _probe.Get(default!, default).ReturnsForAnyArgs(_ => new ProbeResult(answers.Dequeue(), "", "-"));
        var lines = new List<string>();

        Assert.Null(Sut.Smoke(new JsonObject { ["health"] = "https://api/health", ["frontend"] = "https://web" }, lines));

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)], _pause.Sleeps);
        Assert.Equal(["https://api/health answered 200 in 0s, server: - ", "https://web answered 200 in 0s, server: - "], lines);
    }

    [Fact]
    public void Smoke_with_no_health_url_fails_at_once_and_an_api_only_preview_probes_health_alone()
    {
        var lines = new List<string>();
        Assert.Equal("smoke: the workspace has no api_url output", Sut.Smoke(new JsonObject { ["health"] = null }, lines));
        Assert.Equal(["no api_url output"], lines);
        Assert.Equal("smoke: the workspace has no api_url output", Sut.Smoke(null, lines));

        lines.Clear();
        _probe.Get(default!, default).ReturnsForAnyArgs(new ProbeResult(200, new string('x', 300), "Kestrel"));
        Assert.Null(Sut.Smoke(new JsonObject { ["health"] = "https://api/health", ["frontend"] = null }, lines));
        Assert.Equal($"https://api/health answered 200 in 0s, server: Kestrel {new string('x', 200)}", Assert.Single(lines));
    }

    [Fact]
    public void Smoke_reports_the_first_url_that_never_answered_200_and_still_checks_the_rest()
    {
        _o.SmokeSeconds = "25";
        _probe.Get("https://api/health", Arg.Any<TimeSpan>()).Returns(new ProbeResult(0, "connection refused", "-"));
        _probe.Get("https://web", Arg.Any<TimeSpan>()).Returns(new ProbeResult(500, "Backend call failure", "-"));
        var lines = new List<string>();

        Assert.Equal("smoke: https://api/health answered 0", Sut.Smoke(new JsonObject { ["health"] = "https://api/health", ["frontend"] = "https://web" }, lines));

        Assert.Equal(3, _pause.Sleeps.Count);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void SigninSmoke_is_skipped_without_a_QA_user_or_a_front_end()
    {
        var urls = new JsonObject { ["frontend"] = "https://web" };
        Assert.Null(Sut.SigninSmoke(new Dictionary<string, string?> { ["qa_user"] = "u" }, urls, "shots", []));
        Assert.Null(Sut.SigninSmoke(_outs, new JsonObject { ["frontend"] = null }, "shots", []));
        Assert.Null(Sut.SigninSmoke(_outs, null, "shots", []));
        _tools.DidNotReceiveWithAnyArgs().Signin(default!, default!);
    }

    [Fact]
    public void SigninSmoke_signs_in_as_the_QA_user_with_the_password_on_stdin_only()
    {
        _o.SigninSeconds = "45";
        var lines = new List<string>();

        Assert.Null(Sut.SigninSmoke(_outs, new JsonObject { ["frontend"] = "https://web" }, "shots", lines));

        _tools.Received(1).Signin(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "--url", "https://web", "--user", "qa@x.test", "--shots", "shots", "--seconds", "45" })), "pw-1");
        Assert.Contains("shots", Files.Directories);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void SigninSmoke_rides_out_Entras_replication_of_a_new_app()
    {
        // J-025: the smoke failed on AADSTS9002326 and passed about 90 s later.
        var tries = 0;
        _tools.Signin(default!, default!).ReturnsForAnyArgs(_ => ++tries < 3
            ? new ToolResult(1, "error: AADSTS9002326: Cross-origin token redemption")
            : new ToolResult(0, "step: signed in"));
        var lines = new List<string>();

        Assert.Null(Sut.SigninSmoke(_outs, new JsonObject { ["frontend"] = "https://web" }, "shots", lines));

        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)], _pause.Sleeps);
        Assert.Contains("step: try 2 hit Entra replication; retrying in 30 s", lines);
    }

    [Fact]
    public void SigninSmoke_gives_up_after_five_tries_and_fails_at_once_on_any_other_error()
    {
        _tools.Signin(default!, default!).ReturnsForAnyArgs(new ToolResult(1, "error: AADSTS9002326: still"));
        Assert.Equal("sign-in smoke: AADSTS9002326: still", Sut.SigninSmoke(_outs, new JsonObject { ["frontend"] = "https://web" }, "s", []));
        _tools.ReceivedWithAnyArgs(5).Signin(default!, default!);

        _tools.ClearReceivedCalls();
        _tools.Signin(default!, default!).ReturnsForAnyArgs(new ToolResult(2, "step: opened"));
        Assert.Equal("sign-in smoke: exited 2", Sut.SigninSmoke(_outs, new JsonObject { ["frontend"] = "https://web" }, "s", []));
        _tools.ReceivedWithAnyArgs(1).Signin(default!, default!);
    }

    // --- destroy ----------------------------------------------------------------------------------

    [Fact]
    public void RemovePreview_destroys_then_deletes_the_workspace_and_waits_out_HCPs_409()
    {
        _hcp.QueueDestroy("ws-1").Returns(new HcpRun("run-9", "applying"));
        _hcp.Run("run-9").Returns(new HcpRun("run-9", "applying"), new HcpRun("run-9", "applied"));
        var busy = 2;
        _hcp.When(h => h.SafeDelete("ws-1")).Do(_ => { if (busy-- > 0) throw new FactoryException("HCP 409 on POST /workspaces/ws-1/actions/safe-delete: processing"); });

        Assert.Null(Sut.RemovePreview("ws-1"));

        _hcp.Received(3).SafeDelete("ws-1");
        Assert.Equal(4, _pause.Sleeps.Count);
    }

    [Theory]
    [InlineData("planned_and_finished")]
    [InlineData("applied")]
    public void RemovePreview_takes_nothing_left_to_destroy_as_done(string status)
    {
        // J-025: planned_and_finished, the workspace was already empty.
        _hcp.QueueDestroy("ws-1").Returns(new HcpRun("run-9", status));
        Assert.Null(Sut.RemovePreview("ws-1"));
        _hcp.Received(1).SafeDelete("ws-1");
    }

    [Fact]
    public void RemovePreview_deletes_a_workspace_no_apply_ever_reached()
    {
        // J-018: the destroy run is refused with 422 Configuration version is missing.
        var refusal = "HCP 422 on POST /runs: Configuration version is missing";
        _hcp.QueueDestroy("ws-1").Returns(_ => throw new FactoryException(refusal));
        Assert.Null(Sut.RemovePreview("ws-1"));
        _hcp.Received(1).SafeDelete("ws-1");

        refusal = "HCP 500 on POST /runs: down";
        Assert.Equal("HCP 500 on POST /runs: down", Assert.Throws<FactoryException>(() => Sut.RemovePreview("ws-1")).Message);
    }

    [Fact]
    public void A_destroy_that_errors_gives_HCPs_own_first_error_and_keeps_the_workspace()
    {
        // canary-8's said only "errored"; the reason was in HCP's apply log.
        _hcp.QueueDestroy("ws-1").Returns(new HcpRun("run-9", "errored"));
        Assert.Equal("destroy run ended errored: Error: hcp says no", Sut.RemovePreview("ws-1"));
        _hcp.DidNotReceiveWithAnyArgs().SafeDelete(default!);

        _hcp.RunLog("ws-1").Throws(new FactoryException("HCP 500"));
        Assert.Equal("destroy run ended errored", Sut.RemovePreview("ws-1"));
    }

    [Fact]
    public void A_destroy_that_outlives_the_timeout_ends_timed_out_and_a_409_that_outlives_five_minutes_is_thrown()
    {
        _o.TimeoutMinutes = "1";
        _o.PollSeconds = "40";
        _hcp.QueueDestroy("ws-1").Returns(new HcpRun("run-9", "applying"));
        _hcp.Run("run-9").Returns(new HcpRun("run-9", "applying"));
        Assert.StartsWith("destroy run ended timed out", Sut.RemovePreview("ws-1"));

        _hcp.QueueDestroy("ws-1").Returns(new HcpRun("run-9", "applied"));
        _hcp.When(h => h.SafeDelete("ws-1")).Do(_ => throw new FactoryException("HCP 409 on POST x: busy"));
        Assert.Equal("HCP 409 on POST x: busy", Assert.Throws<FactoryException>(() => Sut.RemovePreview("ws-1")).Message);
    }

    [Fact]
    public void Destroy_uses_the_recorded_workspace_else_looks_it_up_and_returns_any_error()
    {
        var (s, j) = InDeploy();
        j.Node["deploy"]!["workspaceId"] = "ws-known";
        Assert.Null(Sut.Destroy(s, j));
        _hcp.Received(1).QueueDestroy("ws-known");
        _hcp.DidNotReceiveWithAnyArgs().WorkspaceId(default!);
        Assert.Equal(1, s.Unlocks);

        j.Node["deploy"]!["workspaceId"] = null;
        _hcp.WorkspaceId("todo-j001").Returns((string?)null);
        Assert.Null(Sut.Destroy(s, j));

        _hcp.WorkspaceId("todo-j001").Throws(new FactoryException("HCP_TOKEN is not set in .env"));
        Assert.Equal("HCP_TOKEN is not set in .env", Sut.Destroy(s, j));
    }
}
