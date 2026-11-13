using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

/// <summary>canary.ps1's canary and canary-destroy, ported to <see cref="CanaryService"/>.</summary>
public class CanaryServiceTests : CommandTestBase
{
    private readonly IDeployService _deploy = Substitute.For<IDeployService>();
    private readonly IHcpClient _hcp = Substitute.For<IHcpClient>();
    private readonly ICanary _canary = Substitute.For<ICanary>();
    private readonly ICanaryLock _lock = Substitute.For<ICanaryLock>();
    private readonly IGitHub _github = Substitute.For<IGitHub>();
    private readonly FakeGit _git = new FakeGit().On("rev-parse HEAD", 0, "scaffold-sha");
    private readonly IToolRunner _tools = Substitute.For<IToolRunner>();
    private readonly IFactoryLauncher _launcher = Substitute.For<IFactoryLauncher>();
    private readonly FakePause _pause;
    private readonly DeployOptions _o = new();
    private readonly IDisposable _held = Substitute.For<IDisposable>();
    private readonly FakeSession _session = FakeSession.With();
    private static readonly string Tfvars = Path.Combine(Work, "canary", "IaC", "envs", "ephemeral.tfvars");
    private string CanaryJson => Path.Combine(Home, "canary.json");

    public CanaryServiceTests()
    {
        _pause = new FakePause(Clock);
        Paths.WorkRoot.Returns(Work);
        DotEnv.Get("HCP_ORG").Returns("org");
        _canary.Fingerprint().Returns("sha256:fp");
        _canary.Repo().Returns("me/factory-canary");
        _canary.Options().Returns(_ => new JsonObject
        {
            ["name"] = "canary", ["resource"] = "Item", ["db"] = "postgres", ["ui"] = "react", ["events"] = false, ["auth"] = true,
            ["entra"] = new JsonObject { ["entra_external_tenant_id"] = "t", ["entra_external_subdomain"] = "s", ["entra_external_user_flow_id"] = "f" },
        });
        _lock.TryTake(out Arg.Any<bool>()).Returns(_held);
        _tools.Pwsh(default!, default!).ReturnsForAnyArgs(c => { Files.Put(Tfvars, "project_name = \"canary\"\nentra_external_subdomain = \"old\"\n"); return new ToolResult(0, ""); });
        _github.Lookup(Arg.Any<string>()).Returns(RepoState.Existing);
        _github.GitAuth().Returns(["-c", "auth"]);
        _github.CloneUrl(Arg.Any<string>()).Returns("https://git/me/factory-canary.git");
        _hcp.EnsureProject("factory-canary").Returns("prj-1");
        _hcp.EnsureWorkspace("factory-canary-j000", "prj-1", Arg.Any<string?>()).Returns("ws-0");
        _hcp.Outputs(default!, default!).ReturnsForAnyArgs(new Dictionary<string, string?> { ["react_url"] = "https://web", ["api_url"] = "https://api" });
        _hcp.RunLog("ws-0").Returns("hcp log");
        _deploy.DeployRun("me/factory-canary", "j000", "main").Returns(new JsonObject { ["id"] = 1, ["conclusion"] = "success" });
        _deploy.Smoke(Arg.Any<JsonObject?>(), Arg.Any<List<string>>()).Returns(c => { c.Arg<List<string>>().Add("https://web/api/health answered 200"); return null; });
        _deploy.SigninSmoke(default!, default, default!, default!).ReturnsForAnyArgs((string?)null);
    }

    private CanaryService Sut => new(_deploy, _hcp, _canary, _lock, _github, _git, _tools, Files, Paths, DotEnv, _launcher, _pause, Clock, Options.Create(_o));

    private JsonObject Canary => JsonNode.Parse(Files.Text(CanaryJson)!)!.AsObject();

    // --- canary -----------------------------------------------------------------------------------

    [Fact]
    public void A_canary_scaffolds_deploys_j000_from_main_records_a_pass_and_starts_its_teardown()
    {
        Assert.Equal("canary: pass (https://api)", Sut.Run(_session, force: false, full: false));

        var c = Canary;
        Assert.Equal(("sha256:fp", "pass", null, "me/factory-canary", "scaffold-sha", "canary-1.log", 1, false),
            ((string?)c["fingerprint"], (string?)c["status"], (string?)c["error"], (string?)c["repo"], (string?)c["sha"], (string?)c["log"], (int)c["n"]!, (bool)c["full"]!));
        Assert.Equal("""{"status":"destroying","error":null,"startedAt":"2026-11-10T12:00:00.000Z","failures":0}""", c["teardown"]!.ToJsonString());
        Assert.Equal("https://web/api/health", (string?)c["urls"]!["health"]);
        Assert.Equal("## Smoke\nhttps://web/api/health answered 200" + Environment.NewLine, Files.Text(Path.Combine(Home, "canary-1.log")));
        Assert.Equal(("canary", "pass"), ((string?)_session.Events.Single()["kind"], (string?)_session.Events.Single()["detail"]));
        Assert.False(_session.Events.Single().ContainsKey("job"));
        Received.InOrder(() => { _held.Dispose(); _launcher.StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "canary-destroy" }))); });
        _deploy.Received(1).PrepareGitHub("me/factory-canary");
        Assert.Equal(1, _session.Unlocks);
    }

    [Fact]
    public void The_scaffold_is_the_bootstrap_commands_with_the_External_ID_ids_in_its_tfvars_and_force_pushed_to_main()
    {
        string? committed = null;
        _git.On("commit", () => { committed = Files.Text(Tfvars); return new Core.Abstractions.GitResult(0, ""); });

        Sut.Run(_session, false, false);

        _tools.Received(1).Pwsh(Path.Combine(Work, "canary"), Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-Command", "dotnet new aspire-base -n canary -o . --dbProvider postgres --resource Item" })));
        _tools.Received(1).Pwsh(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-Command", "aspire-addon add auth" })));
        Assert.Equal(string.Concat(new[] { "project_name = \"canary\"", "entra_external_tenant_id    = \"t\"", "entra_external_subdomain    = \"s\"", "entra_external_user_flow_id = \"f\"" }
            .Select(l => l + Environment.NewLine)), committed);
        Assert.False(Files.DirectoryExists(Path.Combine(Work, "canary")));
        Assert.Equal(["init -q -b main", "add -A", "-c user.name=factory -c user.email=factory@localhost commit -q -m canary scaffold", "rev-parse HEAD",
            "-c auth push -q -f https://git/me/factory-canary.git HEAD:main"], _git.Calls.Select(c => c.Args));
        _github.DidNotReceiveWithAnyArgs().CreateRepo(default!);
    }

    [Fact]
    public void A_stub_scaffold_is_handed_the_commands_and_Full_builds_the_whole_stack()
    {
        _o.CanaryScaffoldCmd = "stub.ps1";
        _canary.Options().Returns(new JsonObject { ["name"] = "canary", ["resource"] = "Item", ["db"] = "sqlite", ["ui"] = "none" });
        _github.Lookup(Arg.Any<string>()).Returns(RepoState.Missing);
        string? committed = null;
        _git.On("commit", () => { committed = Files.Text(Tfvars); return new Core.Abstractions.GitResult(0, ""); });

        Sut.Run(_session, false, full: true);

        _tools.Received(1).Pwsh(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-File", "stub.ps1", "dotnet new aspire-base -n canary -o . --dbProvider sqlite --resource Item" })));
        Assert.EndsWith("use_platform = false" + Environment.NewLine, committed);
        Assert.True((bool)Canary["full"]!);
        _github.Received(1).CreateRepo("me/factory-canary");
    }

    [Fact]
    public void It_is_up_to_date_while_canary_json_records_a_pass_for_the_fingerprint_unless_forced_or_full()
    {
        Files.Put(CanaryJson, """{"fingerprint":"SHA256:FP","status":"pass","n":4}""");

        Assert.Equal("canary: up to date", Sut.Run(_session, false, false));
        _lock.DidNotReceiveWithAnyArgs().TryTake(out _);

        Sut.Run(_session, force: true, full: false);
        Assert.Equal(5, (int)Canary["n"]!);
    }

    [Fact]
    public void It_refuses_with_deploys_off_and_while_another_canary_holds_the_lock()
    {
        _lock.TryTake(out Arg.Any<bool>()).Returns(x => { x[0] = false; return null; });
        Assert.Equal("canary refused: another canary is running (canary.lock)", Assert.Throws<FactoryException>(() => Sut.Run(_session, false, false)).Message);
        _lock.TryTake(out Arg.Any<bool>()).Returns(x => { x[0] = true; return null; });
        Assert.Equal("canary refused: another canary is running on another machine (locks/canary)", Assert.Throws<FactoryException>(() => Sut.Run(_session, false, false)).Message);
        Assert.Null(Files.Text(CanaryJson));

        DotEnv.Get("HCP_ORG").Returns((string?)null);
        Assert.Equal("canary refused: deploys are off (no HCP_ORG in .env)", Assert.Throws<FactoryException>(() => Sut.Run(_session, false, false)).Message);
        _launcher.DidNotReceiveWithAnyArgs().StartDetached(default!);
    }

    [Fact]
    public void Refused_settings_release_the_lock_and_start_no_teardown()
    {
        _canary.Options().Throws(new FactoryException("canary refused: the canary scaffold has auth, and .env lacks FACTORY_ENTRA_TENANT_ID"));

        Assert.StartsWith("canary refused: the canary scaffold has auth", Assert.Throws<FactoryException>(() => Sut.Run(_session, false, false)).Message);

        _held.Received(1).Dispose();
        _launcher.DidNotReceiveWithAnyArgs().StartDetached(default!);
    }

    [Fact]
    public void A_new_canary_waits_for_the_last_ones_teardown_which_still_holds_j000()
    {
        Files.Put(CanaryJson, """{"fingerprint":"old","status":"pass","n":1,"teardown":{"status":"destroying","startedAt":"2026-11-10T11:59:00.000Z","failures":0}}""");
        _o.PollSeconds = "0";
        var looks = 0;
        _pause.Meanwhile = () => { if (++looks == 3) Files.Put(CanaryJson, """{"fingerprint":"old","status":"pass","n":1,"teardown":{"status":"down"}}"""); };

        Sut.Run(_session, false, false);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], _pause.Sleeps);
        Assert.Equal(4, _session.Unlocks);
        Assert.Equal(2, (int)Canary["n"]!);
    }

    [Fact]
    public void A_failed_run_records_its_first_error_logs_GitHub_and_HCP_and_still_starts_the_teardown()
    {
        _deploy.DeployRun("me/factory-canary", "j000", "main").Returns(new JsonObject { ["id"] = 1, ["conclusion"] = "failure" });
        _deploy.RunLog("me/factory-canary", Arg.Any<JsonObject>()).Returns("run x: failure\nError: exec: \"az\": executable file not found");

        Assert.Equal("canary failed: Error: exec: \"az\": executable file not found; see canary-1.log",
            Assert.Throws<FactoryException>(() => Sut.Run(_session, false, false)).Message);

        Assert.Equal(("failed", "Error: exec: \"az\": executable file not found"), ((string?)Canary["status"], (string?)Canary["error"]));
        Assert.StartsWith("## GitHub\nrun x: failure", Files.Text(Path.Combine(Home, "canary-1.log")));
        Assert.Contains("## HCP\nhcp log", Files.Text(Path.Combine(Home, "canary-1.log")));
        Assert.Equal("failed: Error: exec: \"az\": executable file not found", (string?)_session.Events.Single()["detail"]);
        _launcher.Received(1).StartDetached(Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public void A_failed_smoke_or_sign_in_fails_the_canary_with_its_section_in_the_log()
    {
        _deploy.Smoke(Arg.Any<JsonObject?>(), Arg.Any<List<string>>()).Returns("smoke: https://web/api/health answered 503");
        Assert.Throws<FactoryException>(() => Sut.Run(_session, false, false));
        Assert.Equal("smoke: https://web/api/health answered 503", (string?)Canary["error"]);
        _deploy.DidNotReceiveWithAnyArgs().SigninSmoke(default!, default, default!, default!);

        _deploy.Smoke(Arg.Any<JsonObject?>(), Arg.Any<List<string>>()).Returns((string?)null);
        _deploy.SigninSmoke(default!, default, default!, default!).ReturnsForAnyArgs(c => { c.ArgAt<List<string>>(3).Add("error: AADSTS500011"); return "sign-in smoke: AADSTS500011"; });
        Assert.Throws<FactoryException>(() => Sut.Run(_session, true, false));
        Assert.EndsWith("## Sign-in smoke\nError: sign-in smoke: AADSTS500011\nerror: AADSTS500011" + Environment.NewLine, Files.Text(Path.Combine(Home, "canary-2.log")));
        _deploy.Received().SigninSmoke(Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<JsonObject?>(), Path.Combine(Home, "canary-signin"), Arg.Any<List<string>>());
    }

    [Fact]
    public void A_step_that_throws_is_a_Factory_section_with_HCPs_log()
    {
        _deploy.DeployRun("me/factory-canary", "j000", "main").Throws(new FactoryException("cannot dispatch deploy.yml: GitHub 422: no"));

        Assert.Throws<FactoryException>(() => Sut.Run(_session, false, false));

        Assert.Equal("Error: cannot dispatch deploy.yml: GitHub 422: no", (string?)Canary["error"]);
        Assert.Equal("## Factory\nError: cannot dispatch deploy.yml: GitHub 422: no\n\n## HCP\nhcp log" + Environment.NewLine, Files.Text(Path.Combine(Home, "canary-1.log")));
    }

    [Fact]
    public void Scaffold_and_GitHub_failures_stop_the_canary_before_anything_deploys()
    {
        string Error(bool force = true) { Assert.Throws<FactoryException>(() => Sut.Run(_session, force, false)); return (string)Canary["error"]!; }

        _tools.Pwsh(default!, default!).ReturnsForAnyArgs(new ToolResult(1, "template not installed"));
        Assert.Equal("scaffold: dotnet new aspire-base -n canary -o . --dbProvider postgres --resource Item exited 1", Error());
        Assert.StartsWith("## Scaffold\n-Command dotnet new aspire-base", Files.Text(Path.Combine(Home, "canary-1.log")));

        _tools.Pwsh(default!, default!).ReturnsForAnyArgs(new ToolResult(0, ""));
        Assert.Equal("scaffold: no IaC/envs/ephemeral.tfvars to put the Entra ids in", Error());

        _tools.Pwsh(default!, default!).ReturnsForAnyArgs(c => { Files.Put(Tfvars, "x\n"); return new ToolResult(0, ""); });
        _git.On("commit", 1, "nothing to commit");
        Assert.Equal("scaffold: commit failed: nothing to commit", Error());

        _git.On("commit", 0);
        Func<RepoState> lookup = () => throw new FactoryException("GitHub 401: Bad credentials");
        _github.Lookup(Arg.Any<string>()).Returns(_ => lookup());
        Assert.Equal("github: GitHub 401: Bad credentials", Error());

        lookup = () => RepoState.Missing;
        _github.CreateRepo(Arg.Any<string>()).Returns("cannot create me/factory-canary: GitHub 403: no");
        Assert.Equal("github: cannot create me/factory-canary: GitHub 403: no", Error());

        lookup = () => RepoState.Empty;
        _git.On("push", 1, "denied");
        Assert.Equal("github: push failed: denied", Error());
        _deploy.DidNotReceiveWithAnyArgs().PrepareGitHub(default!);
    }

    [Fact]
    public void Full_needs_the_tfvars_to_set_use_platform_in()
    {
        _canary.Options().Returns(new JsonObject { ["name"] = "canary", ["resource"] = "Item", ["db"] = "sqlite", ["ui"] = "none" });
        _tools.Pwsh(default!, default!).ReturnsForAnyArgs(new ToolResult(0, ""));

        Assert.Throws<FactoryException>(() => Sut.Run(_session, false, full: true));

        Assert.Equal("scaffold: no IaC/envs/ephemeral.tfvars to set use_platform in", (string?)Canary["error"]);
    }

    // --- canary-destroy ---------------------------------------------------------------------------

    private void Recorded(string teardown = "") =>
        Files.Put(CanaryJson, $$"""{"fingerprint":"fp","status":"pass","repo":"me/factory-canary","log":"canary-3.log","n":3{{teardown}}}""");

    [Fact]
    public void Teardown_destroys_j000_records_down_appends_to_the_log_and_prints_down()
    {
        Recorded(""","teardown":{"status":"failed","error":"x","startedAt":null,"failures":2}""");
        Files.Put(Path.Combine(Home, "canary-3.log"), "## Smoke\n");
        _hcp.WorkspaceId("factory-canary-j000").Returns("ws-0", (string?)null);

        Assert.Equal(["down"], Sut.Teardown(_session));

        Assert.Equal("""{"status":"down","error":null,"startedAt":"2026-11-10T12:00:00.000Z","failures":0}""", Canary["teardown"]!.ToJsonString());
        Assert.Equal("## Smoke\n\n\n## Destroy\nworkspace factory-canary-j000 destroyed and deleted" + Environment.NewLine, Files.Text(Path.Combine(Home, "canary-3.log")));
        Assert.Equal("""{"ts":"2026-11-10T12:00:00.000Z","job":"canary","kind":"destroy","detail":"down"}""", _session.Events.Single().ToJsonString());
        _deploy.Received(1).RemovePreview("ws-0");
        _held.Received(1).Dispose();
    }

    [Fact]
    public void A_teardown_that_fails_counts_it_and_keeps_HCPs_error()
    {
        Recorded();
        _hcp.WorkspaceId("factory-canary-j000").Returns("ws-0");
        _deploy.RemovePreview("ws-0").Returns("destroy run ended errored: Error: rg still has resources");

        Assert.Equal(["failed"], Sut.Teardown(_session));

        Assert.Equal(("failed", "destroy run ended errored: Error: rg still has resources", 1),
            ((string?)Canary["teardown"]!["status"], (string?)Canary["teardown"]!["error"], (int)Canary["teardown"]!["failures"]!));
        Assert.Equal("failed: destroy run ended errored: Error: rg still has resources", (string?)_session.Events.Single()["detail"]);
    }

    [Fact]
    public void A_workspace_still_there_after_its_destroy_or_an_error_on_the_way_is_a_failed_teardown()
    {
        Recorded();
        _hcp.WorkspaceId("factory-canary-j000").Returns("ws-0");
        Assert.Equal(["failed"], Sut.Teardown(_session));
        Assert.Equal("workspace factory-canary-j000 is still there", (string?)Canary["teardown"]!["error"]);

        _hcp.WorkspaceId("factory-canary-j000").Returns(_ => throw new FactoryException("HCP 500 on GET x: y"));
        Sut.Teardown(_session);
        Assert.Equal(("HCP 500 on GET x: y", 2), ((string?)Canary["teardown"]!["error"], (int)Canary["teardown"]!["failures"]!));
    }

    [Fact]
    public void A_teardown_with_no_workspace_or_no_canary_has_nothing_to_do()
    {
        Assert.Empty(Sut.Teardown(_session));
        Assert.Empty(_session.Events);

        Recorded();
        Assert.Equal(["down"], Sut.Teardown(_session));
        _deploy.DidNotReceiveWithAnyArgs().RemovePreview(default!);
    }

    [Fact]
    public void Teardown_waits_up_to_30_seconds_for_canary_lock_and_refuses_another_machines_lease_at_once()
    {
        _lock.TryTake(out Arg.Any<bool>()).Returns(x => { x[0] = false; return null; });
        Assert.Equal("canary-destroy refused: another canary is running (canary.lock)", Assert.Throws<FactoryException>(() => Sut.Teardown(_session)).Message);
        Assert.Equal(31, _pause.Sleeps.Count);

        _pause.Sleeps.Clear();
        _lock.TryTake(out Arg.Any<bool>()).Returns(x => { x[0] = true; return null; });
        Assert.Equal("canary-destroy refused: another canary is running on another machine (locks/canary)", Assert.Throws<FactoryException>(() => Sut.Teardown(_session)).Message);
        Assert.Empty(_pause.Sleeps);
    }
}
