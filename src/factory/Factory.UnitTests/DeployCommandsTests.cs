using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>The deploy set's commands, ported with spec 09: deploy, deploy-log, deploy-fix, block, redeploy, destroy, sweep, qa-login, canary.</summary>
public class DeployCommandsTests : CommandTestBase
{
    private readonly IDeployService _deploy = Substitute.For<IDeployService>();
    private readonly IHcpClient _hcp = Substitute.For<IHcpClient>();
    private readonly IFactoryLauncher _launcher = Substitute.For<IFactoryLauncher>();
    private readonly DeployOptions _o = new();

    private static readonly JsonObject Urls = new() { ["frontend"] = "https://web", ["api"] = "https://api", ["health"] = "https://web/api/health" };

    public DeployCommandsTests() => DotEnv.Get("HCP_ORG").Returns("org");

    private DeployCommand Deploy => new(_deploy, Paths, Files, DotEnv, Clock);

    private static FakeSession InDeploy(JsonObject? deploy = null) =>
        FakeSession.With(Jobs.New("J-001", "deploy", target: "o/todo").With("deploy", deploy));

    private void Attempt(DeployAttempt r) => _deploy.Attempt(default!, default!, default, default).ReturnsForAnyArgs(r);

    private static DeployAttempt Failed(string log) => new(false, "sha-2", "https://gh/runs/2", null, "2026-11-11T12:00:00Z", "ws-1", log);

    // --- deploy -----------------------------------------------------------------------------------

    [Fact]
    public void A_passing_attempt_records_the_preview_up_and_moves_the_job_to_approve()
    {
        var s = InDeploy();
        Attempt(new DeployAttempt(true, "sha-1", "https://gh/runs/1", Urls.DeepClone().AsObject(), "2026-11-11T12:00:00Z", "ws-1", ""));

        var r = Deploy.Run(Args("deploy", "J-001"), s);

        Assert.Equal(["frontend: https://web", "api: https://api"], r.Out);
        var j = s.Job("J-001");
        Assert.Equal("approve", (string?)j["stage"]);
        Assert.Equal("""{"env":"j001","workspace":"todo-j001","workspaceId":"ws-1","status":"up","attempts":1,"sha":"sha-1","run":"https://gh/runs/1","urls":{"frontend":"https://web","api":"https://api","health":"https://web/api/health"},"appliedAt":"2026-11-10T12:00:00.000Z","destroyAt":"2026-11-11T12:00:00Z"}""",
            j["deploy"]!.ToJsonString());
        Assert.Equal(["deploy", "stage"], s.Kinds);
        Assert.Equal("up https://web", (string?)s.Events[0]["detail"]);
        _deploy.Received(1).Attempt(s, Arg.Is<Job>(x => x.Id == "J-001"), false, null);
    }

    [Fact]
    public void An_api_only_preview_is_announced_by_its_api()
    {
        var s = InDeploy();
        Attempt(new DeployAttempt(true, "sha-1", "r", new JsonObject { ["frontend"] = null, ["api"] = "https://api" }, "t", "ws-1", ""));

        Assert.Equal(["frontend: ", "api: https://api"], Deploy.Run(Args("deploy", "J-001"), s).Out);
        Assert.Equal("up https://api", (string?)s.Events[0]["detail"]);
    }

    [Fact]
    public void Each_attempt_is_counted_and_saved_as_applying_before_it_starts()
    {
        var s = InDeploy(new JsonObject { ["env"] = "j001", ["workspace"] = "todo-j001", ["status"] = "failed", ["attempts"] = 2 });
        string? during = null;
        _deploy.Attempt(default!, default!, default, default).ReturnsForAnyArgs(_ => { during = s.Saved; return Failed("## GitHub\nError: boom"); });

        Assert.Throws<FactoryException>(() => Deploy.Run(Args("deploy", "J-001"), s));

        Assert.Contains("\"status\":\"applying\",\"attempts\":3", during);
    }

    [Fact]
    public void A_deploy_whose_process_died_resumes_that_attempt_without_counting_it()
    {
        var s = InDeploy(new JsonObject { ["env"] = "j001", ["workspace"] = "todo-j001", ["status"] = "applying", ["attempts"] = 5 });
        Attempt(new DeployAttempt(true, "sha-1", "r", Urls.DeepClone().AsObject(), "t", null, ""));

        Deploy.Run(Args("deploy", "J-001"), s);

        _deploy.Received(1).Attempt(s, Arg.Any<Job>(), true, null);
        Assert.Equal(5, (int)s.Job("J-001")["deploy"]!["attempts"]!);
        Assert.False(s.Job("J-001")["deploy"]!.AsObject().ContainsKey("workspaceId"));
    }

    [Fact]
    public void A_redeploy_over_a_live_preview_offers_the_head_it_was_applied_at_as_since()
    {
        var s = InDeploy(new JsonObject { ["status"] = "up", ["sha"] = "sha-0", ["attempts"] = 0, ["workspace"] = "todo-j001", ["env"] = "j001" });
        Attempt(new DeployAttempt(true, "sha-1", "r", Urls.DeepClone().AsObject(), "t", "ws-1", ""));

        Deploy.Run(Args("deploy", "J-001"), s);

        _deploy.Received(1).Attempt(s, Arg.Any<Job>(), false, "sha-0");
    }

    [Fact]
    public void After_a_failed_attempt_a_redeploy_offers_no_since()
    {
        // A failed attempt may have left the preview half applied, so the next one deploys everything.
        var s = InDeploy(new JsonObject { ["status"] = "failed", ["sha"] = "sha-0", ["attempts"] = 1, ["workspace"] = "todo-j001", ["env"] = "j001" });
        Attempt(new DeployAttempt(true, "sha-1", "r", Urls.DeepClone().AsObject(), "t", "ws-1", ""));

        Deploy.Run(Args("deploy", "J-001"), s);

        _deploy.Received(1).Attempt(s, Arg.Any<Job>(), false, null);
    }

    [Fact]
    public void A_failed_attempt_writes_its_log_records_the_first_error_and_exits_1_in_deploy()
    {
        var s = InDeploy();
        Attempt(Failed("## GitHub\nrun x: failure\nError: exec: az not found"));

        var e = Assert.Throws<FactoryException>(() => Deploy.Run(Args("deploy", "J-001"), s));

        Assert.Equal("deploy attempt 1 failed: Error: exec: az not found; see deploy-1.log (factory deploy-log J-001)", e.Message);
        Assert.Equal(1, e.ExitCode);
        var j = s.Job("J-001");
        Assert.Equal(("deploy", "failed", "sha-2", "ws-1"), ((string?)j["stage"], (string?)j["deploy"]!["status"], (string?)j["deploy"]!["sha"], (string?)j["deploy"]!["workspaceId"]));
        Assert.Equal("## GitHub\nrun x: failure\nError: exec: az not found" + Environment.NewLine, Files.Text(JobFile("J-001", "deploy-1.log")));
        Assert.Equal("failed (attempt 1): Error: exec: az not found", (string?)s.Events.Single()["detail"]);
    }

    [Fact]
    public void The_fifth_failed_attempt_blocks_the_job_deploy_failed_and_a_sixth_is_refused()
    {
        var s = InDeploy(new JsonObject { ["env"] = "j001", ["workspace"] = "todo-j001", ["status"] = "failed", ["attempts"] = 4 });
        Attempt(Failed("Error: boom"));

        Assert.Throws<FactoryException>(() => Deploy.Run(Args("deploy", "J-001"), s));

        var j = s.Job("J-001");
        Assert.Equal(("blocked", "deploy-failed", "Error: boom"), ((string?)j["stage"], (string?)j["flag"]!["reason"], (string?)j["flag"]!["detail"]));

        j["stage"] = "deploy";
        Assert.Equal("deploy refused: J-001 has used its 5 attempts", Assert.Throws<FactoryException>(() => Deploy.Run(Args("deploy", "J-001"), s)).Message);
    }

    [Fact]
    public void A_missing_GitHub_PAT_permission_gives_the_attempt_back_and_blocks_the_job_naming_it()
    {
        var s = InDeploy();
        Attempt(Failed($"## Factory\nError: cannot create the preview environment: GitHub 403: no; {GitHubErrors.AppNeeds("environments=write")}"));

        var e = Assert.Throws<FactoryException>(() => Deploy.Run(Args("deploy", "J-001"), s));

        Assert.Equal($"deploy refused by GitHub: {GitHubErrors.AppNeeds("environments=write")}; then run: factory redeploy J-001", e.Message);
        var j = s.Job("J-001");
        Assert.Equal(("blocked", "github-permission", 0), ((string?)j["stage"], (string?)j["flag"]!["reason"], (int)j["deploy"]!["attempts"]!));
        Assert.Equal(GitHubErrors.AppNeeds("environments=write"), (string?)j["flag"]!["detail"]);
    }

    [Fact]
    public void A_bootstrap_skips_the_deploy_whatever_its_attempts_and_shows_no_stale_failure()
    {
        // J-018: in deploy with three attempts spent on GitHub's 404 for a deploy.yml not yet on main.
        var s = FakeSession.With(Jobs.New("J-001", "deploy").With("kind", "bootstrap").With("deploy", new JsonObject { ["attempts"] = 3, ["status"] = "failed" }));

        var r = Deploy.Run(Args("deploy", "J-001"), s);

        Assert.Equal([$"skipped: {Verdicts.BootstrapNoDeploy}"], r.Out);
        Assert.Equal(("approve", "down"), ((string?)s.Job("J-001")["stage"], (string?)s.Job("J-001")["deploy"]!["status"]));
        Assert.Equal(["stage", "note"], s.Kinds);
        _deploy.DidNotReceiveWithAnyArgs().Attempt(default!, default!, default, default);

        var fresh = FakeSession.With(Jobs.New("J-002", "deploy").With("kind", "bootstrap"));
        Deploy.Run(Args("deploy", "J-002"), fresh);
        Assert.Null(fresh.Job("J-002")["deploy"]);
    }

    [Fact]
    public void Deploy_refuses_outside_deploy_and_with_deploys_off()
    {
        Assert.Equal("deploy refused: J-001 is in 'approved', needs deploy",
            Assert.Throws<FactoryException>(() => Deploy.Run(Args("deploy", "J-001"), FakeSession.With(Jobs.New("J-001", "approved")))).Message);
        DotEnv.Get("HCP_ORG").Returns((string?)null);
        Assert.Equal("deploy refused: deploys are off (no HCP_ORG in .env)",
            Assert.Throws<FactoryException>(() => Deploy.Run(Args("deploy", "J-001"), InDeploy())).Message);
    }

    [Fact]
    public void Deploy_runs_in_a_tool_install_since_spec_16()
    {
        Paths.IsToolInstall.Returns(true);

        var e = Record.Exception(() => Deploy.Run(Args("deploy", "J-001"), InDeploy()));

        Assert.DoesNotContain("checkout install", e?.Message ?? "", StringComparison.Ordinal);
    }

    // --- deploy-log -------------------------------------------------------------------------------

    [Fact]
    public void DeployLog_prints_the_last_attempts_log_by_number()
    {
        var s = InDeploy();
        Files.Put(JobFile("J-001", "deploy-2.log"), "two\n");
        Files.Put(JobFile("J-001", "deploy-10.log"), "ten\n");
        Files.Put(JobFile("J-001", "round-1/deploy-99.log"), "elsewhere\n");
        Files.Put(JobFile("J-001", "smoke-11.log"), "smoke\n");

        Assert.Equal(["ten\n"], new DeployLogCommand(Paths, Files).Run(Args("deploy-log", "J-001"), s).Out);
    }

    [Fact]
    public void DeployLog_refuses_a_job_with_no_failed_deploy() =>
        Assert.Equal("J-001 has no failed deploy",
            Assert.Throws<FactoryException>(() => new DeployLogCommand(Paths, Files).Run(Args("deploy-log", "J-001"), InDeploy())).Message);

    // --- deploy-fix and block ---------------------------------------------------------------------

    private static FakeSession FailedDeploy(int fixes = 0) =>
        FakeSession.With(Jobs.New("J-001", "deploy").With("deployFixes", fixes).With("deploy", new JsonObject { ["status"] = "failed", ["attempts"] = 2 })
            .With("reviews", new JsonObject { ["security"] = "pass", ["code"] = "pass", ["ux"] = "pass", ["ui"] = "pass" }));

    private DeployFixCommand Fix => new(Paths, Files, DotEnv, Options.Create(new FactoryOptions { WorkingDirectory = Work }), Clock);

    [Fact]
    public void DeployFix_sends_the_job_back_to_build_as_a_new_round_with_its_diagnosis()
    {
        var s = FailedDeploy();
        Files.Put(Path.Combine(Work, "diag.md"), "The migration fails.\nDrop the duplicate.");

        Assert.Equal(["build"], Fix.Run(Args("deploy-fix", "J-001", "-File", "diag.md"), s).Out);

        var j = s.Job("J-001");
        Assert.Equal((2, 1, 0, "pending"), ((int)j["round"]!, (int)j["deployFixes"]!, (int)j["deploy"]!["attempts"]!, (string?)j["reviews"]!["code"]));
        Assert.Equal("The migration fails.\nDrop the duplicate.", Files.Text(JobFile("J-001", "deploy-fix-2.md")));
    }

    [Fact]
    public void The_third_deploy_fix_blocks_the_job_with_its_first_line_and_starts_no_round()
    {
        var s = FailedDeploy(fixes: 2);
        Files.Put(Path.Combine(Work, "diag.md"), "The migration fails.  \nmore");

        Assert.Equal(["blocked"], Fix.Run(Args("deploy-fix", "J-001", "-File", "diag.md"), s).Out);

        var j = s.Job("J-001");
        Assert.Equal(("deploy-fix-rounds", "The migration fails.", 1), ((string?)j["flag"]!["reason"], (string?)j["flag"]!["detail"], (int)j["round"]!));
    }

    [Fact]
    public void DeployFix_needs_a_diagnosis_file_and_a_failed_attempt()
    {
        Assert.Equal("deploy-fix refused: diagnosis file not found: nope.md", Assert.Throws<FactoryException>(() => Fix.Run(Args("deploy-fix", "J-001", "-File", "nope.md"), FailedDeploy())).Message);
        Assert.Equal("deploy-fix refused: diagnosis file not found: ", Assert.Throws<FactoryException>(() => Fix.Run(Args("deploy-fix", "J-001"), FailedDeploy())).Message);
        Assert.Equal("deploy-fix refused: J-001 has no failed deploy attempt",
            Assert.Throws<FactoryException>(() => Fix.Run(Args("deploy-fix", "J-001", "-File", "x"), InDeploy(new JsonObject { ["status"] = "up" }))).Message);
    }

    [Fact]
    public void Block_parks_the_job_flagged_environment_with_the_reason()
    {
        var s = FailedDeploy();
        var block = new BlockCommand(DotEnv, Clock);

        Assert.Equal(["blocked"], block.Run(Args("block", "J-001", "-Detail", "the region is out of B1ms capacity"), s).Out);
        Assert.Equal(("environment", "the region is out of B1ms capacity"), ((string?)s.Job("J-001")["flag"]!["reason"], (string?)s.Job("J-001")["flag"]!["detail"]));

        Assert.Equal("usage: block <id> -Detail <one line>", Assert.Throws<FactoryException>(() => block.Run(Args("block", "J-001"), FailedDeploy())).Message);
        Assert.Equal("block refused: J-001 has no failed deploy attempt", Assert.Throws<FactoryException>(() => block.Run(Args("block", "J-001", "-Detail", "x"), InDeploy())).Message);
    }

    [Fact]
    public void Block_with_deploys_off_lands_in_ready()
    {
        DotEnv.Get("HCP_ORG").Returns((string?)null);
        Assert.Equal(["ready"], new BlockCommand(DotEnv, Clock).Run(Args("block", "J-001", "-Detail", "x"), FailedDeploy()).Out);
    }

    // --- redeploy ---------------------------------------------------------------------------------

    [Fact]
    public void Redeploy_queues_the_job_to_resume_at_deploy_with_a_fresh_attempt_budget()
    {
        var job = Jobs.New("J-001", "blocked").With("round", 2).With("deployFixes", 1)
            .With("flag", new JsonObject { ["reason"] = "environment", ["detail"] = "quota" })
            .With("deploy", new JsonObject { ["status"] = "failed", ["attempts"] = 5 })
            .With("history", new JsonArray(new JsonObject { ["from"] = "deploy", ["to"] = "blocked" }));
        var s = FakeSession.With(job);

        Assert.Empty(new RedeployCommand(Clock).Run(Args("redeploy", "J-001"), s).Out);

        var j = s.Job("J-001");
        Assert.Equal(("approved", "deploy", 0, 2, 1, Now, null), ((string?)j["stage"], (string?)j["resume"], (int)j["deploy"]!["attempts"]!, (int)j["round"]!, (int)j["deployFixes"]!, (string?)j["approvedAt"], j["flag"]));
        Assert.Equal("human", (string?)s.Events.Single()["by"]);
    }

    [Fact]
    public void Redeploy_of_a_job_with_no_deploy_record_and_a_refusal()
    {
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("flag", new JsonObject { ["reason"] = "github-permission" }));
        new RedeployCommand(Clock).Run(Args("redeploy", "J-001"), s);
        Assert.Null(s.Job("J-001")["deploy"]);

        Assert.Throws<FactoryException>(() => new RedeployCommand(Clock).Run(Args("redeploy", "J-001"), FakeSession.With(Jobs.New("J-001", "approved"))));
    }

    // --- destroy ----------------------------------------------------------------------------------

    [Fact]
    public void Destroy_records_down_and_clears_the_urls_and_any_last_failure()
    {
        var s = FakeSession.With(Jobs.New("J-001", "done").With("deploy", new JsonObject
        {
            ["status"] = "failed", ["urls"] = Urls.DeepClone(), ["destroyStartedAt"] = "x", ["destroyError"] = "old", ["destroyFailures"] = 2,
        }));
        string? during = null;
        _deploy.Destroy(default!, default!).ReturnsForAnyArgs(_ => { during = s.Saved; return null; });

        Assert.Equal(["down"], new DestroyCommand(_deploy, Clock).Run(Args("destroy", "J-001"), s).Out);

        Assert.Contains("\"status\":\"destroying\"", during);
        Assert.Equal("""{"status":"down","urls":null,"destroyStartedAt":"2026-11-10T12:00:00.000Z","destroyError":null,"destroyFailures":0}""", s.Job("J-001")["deploy"]!.ToJsonString());
        Assert.Equal(("destroy", "down"), ((string?)s.Events.Single()["kind"], (string?)s.Events.Single()["detail"]));
        Assert.Equal("done", (string?)s.Job("J-001")["stage"]);
    }

    [Theory]
    [InlineData("up", "up")]
    [InlineData("destroying", "failed")]
    public void A_failed_destroy_puts_the_status_back_counts_it_and_keeps_its_error(string status, string after)
    {
        var s = FakeSession.With(Jobs.New("J-001", "discarded").With("deploy", new JsonObject { ["status"] = status }));
        _deploy.Destroy(default!, default!).ReturnsForAnyArgs("destroy run ended errored: Error: rg");

        Assert.Equal("destroy failed: destroy run ended errored: Error: rg", Assert.Throws<FactoryException>(() => new DestroyCommand(_deploy, Clock).Run(Args("destroy", "J-001"), s)).Message);

        var d = s.Job("J-001")["deploy"]!;
        Assert.Equal((after, "destroy run ended errored: Error: rg", 1), ((string?)d["status"], (string?)d["destroyError"], (int)d["destroyFailures"]!));
        Assert.Equal("failed: destroy run ended errored: Error: rg", (string?)s.Events.Single()["detail"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("down")]
    public void Destroy_is_a_no_op_without_a_preview(string? status)
    {
        var s = FakeSession.With(Jobs.New("J-001", "done").With("deploy", status is null ? null : new JsonObject { ["status"] = status }));
        Assert.Equal(["no preview for J-001"], new DestroyCommand(_deploy, Clock).Run(Args("destroy", "J-001"), s).Out);
        Assert.Equal(0, s.Saves);
        _deploy.DidNotReceiveWithAnyArgs().Destroy(default!, default!);
    }

    // --- sweep ------------------------------------------------------------------------------------

    private SweepCommand Sweeper => new(DotEnv, Paths, Files, _launcher, Clock, Options.Create(_o));

    [Fact]
    public void Sweep_starts_a_destroy_for_each_finished_job_whose_preview_is_not_down()
    {
        var s = FakeSession.With(
            Jobs.New("J-001", "done").With("deploy", new JsonObject { ["status"] = "up" }),
            Jobs.New("J-002", "discarded").With("deploy", new JsonObject { ["status"] = "failed", ["destroyFailures"] = 5 }),
            Jobs.New("J-003", "discarded").With("deploy", new JsonObject { ["status"] = "down" }),
            Jobs.New("J-004", "approve").With("deploy", new JsonObject { ["status"] = "up" }),
            Jobs.New("J-005", "done"),
            Jobs.New("J-006", "done").With("deploy", new JsonObject { ["status"] = "destroying", ["destroyStartedAt"] = "2026-11-10T11:30:00.000Z" }));

        Assert.Equal(["J-001"], Sweeper.Run(Args("sweep"), s).Out);

        _launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "destroy", "J-001" })));
        _launcher.ReceivedWithAnyArgs(1).StartDetached(default!);
    }

    [Fact]
    public void Sweep_retries_a_failed_canary_teardown_and_does_nothing_with_deploys_off()
    {
        Files.Put(Path.Combine(Home, "canary.json"), """{"status":"pass","teardown":{"status":"failed","startedAt":"x","failures":1}}""");

        Assert.Equal(["canary"], Sweeper.Run(Args("sweep"), FakeSession.With()).Out);
        _launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "canary-destroy" })));

        Files.Put(Path.Combine(Home, "canary.json"), """{"status":"pass","teardown":{"status":"down"}}""");
        Assert.Empty(Sweeper.Run(Args("sweep"), FakeSession.With()).Out);

        DotEnv.Get("HCP_ORG").Returns((string?)null);
        Files.Put(Path.Combine(Home, "canary.json"), """{"teardown":{"status":"failed"}}""");
        Assert.Empty(Sweeper.Run(Args("sweep"), FakeSession.With(Jobs.New("J-001", "done").With("deploy", new JsonObject { ["status"] = "up" }))).Out);
        _launcher.ReceivedWithAnyArgs(1).StartDetached(default!);
    }

    // --- qa-login ---------------------------------------------------------------------------------

    [Fact]
    public void QaLogin_reads_the_QA_user_and_password_from_the_recorded_workspace()
    {
        var s = FakeSession.With(Jobs.New("J-001", "approve").With("deploy", new JsonObject { ["status"] = "up", ["workspace"] = "todo-j001", ["workspaceId"] = "ws-1" }));
        _hcp.Outputs("ws-1", Arg.Is<IReadOnlyCollection<string>>(c => c.SequenceEqual(new[] { "qa_password" })))
            .Returns(new Dictionary<string, string?> { ["qa_user"] = "qa@x.test", ["qa_password"] = "pw" });

        Assert.Equal(["user: qa@x.test", "password: pw"], new QaLoginCommand(_hcp).Run(Args("qa-login", "J-001"), s).Out);
        _hcp.DidNotReceiveWithAnyArgs().WorkspaceId(default!);
    }

    [Fact]
    public void QaLogin_looks_the_workspace_up_and_refuses_without_a_live_preview_or_a_QA_user()
    {
        var sut = new QaLoginCommand(_hcp);
        Assert.Equal("qa-login refused: J-001 has no live preview",
            Assert.Throws<FactoryException>(() => sut.Run(Args("qa-login", "J-001"), FakeSession.With(Jobs.New("J-001", "ready")))).Message);
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("deploy", new JsonObject { ["status"] = "up", ["workspace"] = "todo-j001" }));
        Assert.Equal("qa-login refused: the preview has no QA user (no auth add-on)", Assert.Throws<FactoryException>(() => sut.Run(Args("qa-login", "J-001"), s)).Message);
        _hcp.Received(1).WorkspaceId("todo-j001");

        _hcp.WorkspaceId("todo-j001").Returns("ws-2");
        _hcp.Outputs("ws-2", Arg.Any<IReadOnlyCollection<string>>()).Returns(new Dictionary<string, string?> { ["api_url"] = "x" });
        Assert.Throws<FactoryException>(() => sut.Run(Args("qa-login", "J-001"), s));
    }

    // --- canary -----------------------------------------------------------------------------------

    [Fact]
    public void Canary_and_canary_destroy_run_the_canary_service_with_their_switches()
    {
        var canary = Substitute.For<ICanaryService>();
        var s = FakeSession.With();
        canary.Run(s, true, true).Returns("canary: pass (https://api)");
        canary.Teardown(s).Returns(["down"]);

        Assert.Equal(["canary: pass (https://api)"], new CanaryCommand(canary).Run(Args("canary", "-Force", "-Full"), s).Out);
        Assert.Equal(["down"], new CanaryDestroyCommand(canary).Run(Args("canary-destroy"), s).Out);
        Assert.Equal(["canary", "canary-destroy"], [new CanaryCommand(canary).Name, new CanaryDestroyCommand(canary).Name]);
    }
}
