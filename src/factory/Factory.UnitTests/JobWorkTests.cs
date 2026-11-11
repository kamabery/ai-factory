using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;

namespace Factory.UnitTests;

public class JobWorkTests : CommandTestBase
{
    private readonly FakeGit _git = new();
    private readonly IGitHub _gh = Substitute.For<IGitHub>();
    private readonly IGateShell _shell = Substitute.For<IGateShell>();
    private readonly IFactoryLauncher _launcher = Substitute.For<IFactoryLauncher>();
    private readonly FactoryOptions _options = new();
    private static readonly string Wt = Path.Combine(Work, "jobs", "J-001");

    public JobWorkTests()
    {
        Paths.WorkRoot.Returns(Work);
        _gh.GitAuth().Returns(["-c", "auth"]);
        _gh.CloneUrl(Arg.Any<string>()).Returns(c => $"https://gh/{c.Arg<string>()}.git");
        _gh.Lookup(Arg.Any<string>()).Returns(RepoState.Existing);
        _gh.Send(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>()).Returns(new GitHubResponse(200, null, null));
    }

    private JobWork Sut => new(Paths, Files, _git, _gh, _shell, _launcher, Options.Create(_options), Clock);

    private void Clone(string id = "J-001") => Files.Directories.Add(Path.Combine(Work, "jobs", id, ".git"));

    // --- Ensure-Clone ----------------------------------------------------------------------------

    [Fact]
    public void A_clone_already_on_its_branch_is_left_as_it_is()
    {
        Clone();
        _git.On("branch --show-current", 0, "factory/J-001");

        Assert.Null(Sut.EnsureClone(FakeSession.With(Jobs.New("J-001", "build")).Board, "J-001"));
        Assert.Empty(_git.Ran("clone"));
        Assert.Empty(_git.Ran("checkout"));
    }

    [Fact]
    public void Clones_an_existing_target_with_the_credential_and_branches_from_origin_main()
    {
        Files.Put(Path.Combine(Wt, "junk.txt"), "x");
        _git.On("rev-parse --verify --quiet refs/remotes/origin/factory/J-001", 1);

        Assert.Null(Sut.EnsureClone(FakeSession.With(Jobs.New("J-001", "build")).Board, "J-001"));

        Assert.Null(Files.Text(Path.Combine(Wt, "junk.txt")));
        Assert.Equal((Path.Combine(Work, "jobs"), $"-c auth clone -q https://gh/o/r.git {Wt}"), _git.Ran("clone").Single());
        Assert.Equal("checkout -q -b factory/J-001 origin/main", _git.Ran("checkout").Single().Args);
    }

    [Fact]
    public void A_pushed_branch_is_checked_out_again_after_a_rework()
    {
        Clone();

        Assert.Null(Sut.EnsureClone(FakeSession.With(Jobs.New("J-001", "build")).Board, "J-001"));

        Assert.Equal("checkout -q -B factory/J-001 origin/factory/J-001", _git.Ran("checkout").Single().Args);
    }

    [Fact]
    public void A_stacked_job_branches_from_its_unmerged_bases_branch()
    {
        Clone();
        _git.On("refs/remotes/origin/factory/J-001", 1);
        var board = FakeSession.With(Jobs.New("J-002", "ready"), Jobs.New("J-001", "build").With("base", "J-002")).Board;

        Assert.Null(Sut.EnsureClone(board, "J-001"));

        Assert.Equal("checkout -q -b factory/J-001 origin/factory/J-002", _git.Ran("checkout").Single().Args);
    }

    [Fact]
    public void Says_why_it_cannot_clone_or_check_out()
    {
        var board = FakeSession.With(Jobs.New("J-001", "build"), Jobs.New("J-002", "build", target: @"D:\code"),
            Jobs.New("J-003", "discarded"), Jobs.New("J-004", "build").With("base", "J-003")).Board;
        Assert.Equal(@"target 'D:\code' is not a GitHub repo written owner/name", Sut.EnsureClone(board, "J-002"));

        _gh.Lookup("o/r").Throws(new FactoryException("GitHub 401: Bad credentials"));
        Assert.Equal("GitHub 401: Bad credentials", Sut.EnsureClone(board, "J-001"));

        _gh.Configure().Lookup("o/r").Returns(RepoState.Existing);
        _git.On("clone", 128, "fatal: nope");
        Assert.Equal("clone of o/r failed: fatal: nope", Sut.EnsureClone(board, "J-001"));

        Clone();
        _git.On("branch --show-current", 0, "main").On("status --porcelain", 0, " M a.txt");
        Assert.Equal($"work dir {Wt} has uncommitted changes on 'main'", Sut.EnsureClone(board, "J-001"));

        Clone("J-004");
        _git.On("status --porcelain", 0, "");
        Assert.Equal("J-004 is stacked on J-003, which was discarded", Sut.EnsureClone(board, "J-004"));

        _git.On("rev-parse --verify --quiet", 1);
        Assert.Equal("o/r has no main branch", Sut.EnsureClone(board, "J-001"));

        _git.On("rev-parse --verify --quiet refs/remotes/origin/main", 0).On("checkout", 1, "error: x");
        Assert.Equal("checkout factory/J-001 failed: error: x", Sut.EnsureClone(board, "J-001"));
    }

    [Fact]
    public void A_missing_target_is_created_and_main_seeded_with_an_empty_commit_then_cloned()
    {
        _gh.Lookup("o/r").Returns(RepoState.Missing);
        _gh.CreateRepo("o/r").Returns((string?)null);

        Assert.Null(Sut.EnsureClone(FakeSession.With(Jobs.New("J-001", "build")).Board, "J-001"));

        var seed = _git.Ran("init -q -b main").Single().Dir;
        Assert.StartsWith(Path.Combine(Work, "seed-"), seed);
        Assert.Equal(Path.Combine(Work, "seed-").Length + 8, seed.Length);
        Assert.Equal(
            ["init -q -b main", "-c user.name=factory -c user.email=factory@localhost commit -q --allow-empty -m Initial commit (factory)", "-c auth push -q https://gh/o/r.git main"],
            _git.Calls.Where(c => c.Dir == seed).Select(c => c.Args));
        Assert.Single(_git.Ran("clone"));
    }

    [Fact]
    public void An_empty_target_is_seeded_without_creating_it_and_a_refusal_says_why()
    {
        _gh.Lookup("o/r").Returns(RepoState.Empty);
        _git.On("push", 1, "denied");

        Assert.Equal("initial commit for o/r failed: denied", Sut.EnsureClone(FakeSession.With(Jobs.New("J-001", "build")).Board, "J-001"));
        _gh.DidNotReceive().CreateRepo(Arg.Any<string>());

        _gh.Lookup("o/r").Returns(RepoState.Missing);
        _gh.CreateRepo("o/r").Returns("cannot create o/r: GitHub 403: no");
        Assert.Equal("cannot create o/r: GitHub 403: no", Sut.EnsureClone(FakeSession.With(Jobs.New("J-001", "build")).Board, "J-001"));
    }

    [Fact]
    public void Fetch_runs_with_the_credential()
    {
        Sut.Fetch(Wt, "main");

        Assert.Equal((Wt, "-c auth fetch -q origin main"), _git.Calls.Single());
    }

    // --- Publish-Job -----------------------------------------------------------------------------

    [Fact]
    public void Publish_pushes_and_opens_one_PR_into_the_base_branch_and_records_it()
    {
        Clone();
        _gh.Send(HttpMethod.Post, "/repos/o/r/pulls", Arg.Any<JsonNode?>())
            .Returns(new GitHubResponse(201, JsonNode.Parse("""{"number":4,"html_url":"https://gh/o/r/pull/4"}"""), null));
        var s = FakeSession.With(Jobs.New("J-001", "ready"));

        var (url, err) = Sut.Publish(s, new Job(s.Job("J-001")));

        Assert.Equal(("https://gh/o/r/pull/4", (string?)null), (url, err));
        Assert.Equal("-c auth push -q origin factory/J-001:factory/J-001", _git.Ran("push").Single().Args);
        _gh.Received(1).Send(HttpMethod.Post, "/repos/o/r/pulls", Arg.Is<JsonNode?>(b =>
            b!.ToJsonString() == """{"title":"J-001: Thing","head":"factory/J-001","base":"main","body":"Built by the factory from spec J-001. Merged when a human moves it to Done."}"""));
        Assert.Equal("""{"number":4,"url":"https://gh/o/r/pull/4"}""", s.Job("J-001")["pr"]!.ToJsonString());
        Assert.Equal("PR opened: https://gh/o/r/pull/4", (string?)s.Events.Single()["detail"]);
        Assert.Equal(1, s.Saves);
    }

    [Fact]
    public void Publish_again_reuses_the_open_PR()
    {
        Clone();
        _gh.OpenPr("o/r", "factory/J-001").Returns(new PullRequest(4, "u4"));
        var s = FakeSession.With(Jobs.New("J-001", "ready"));

        Assert.Equal(("u4", (string?)null), Sut.Publish(s, new Job(s.Job("J-001"))));

        Assert.Empty(s.Events);
        _gh.DidNotReceive().Send(HttpMethod.Post, Arg.Any<string>(), Arg.Any<JsonNode?>());
    }

    [Fact]
    public void Publish_says_why_not()
    {
        var s = FakeSession.With(Jobs.New("J-001", "ready"));
        var j = new Job(s.Job("J-001"));
        Assert.Equal((null, "J-001 has no work dir to publish"), Sut.Publish(s, j));

        Clone();
        _git.On("push", 1, "rejected");
        Assert.Equal((null, "push failed: rejected"), Sut.Publish(s, j));

        _git.On("push", 0);
        _gh.Send(HttpMethod.Post, Arg.Any<string>(), Arg.Any<JsonNode?>()).Returns(new GitHubResponse(422, JsonNode.Parse("""{"message":"No commits"}"""), null));
        Assert.Equal((null, "cannot open the PR: GitHub 422: No commits"), Sut.Publish(s, j));
        Assert.Equal(0, s.Saves);
    }

    // --- the gate --------------------------------------------------------------------------------

    [Fact]
    public void Gate_commands_come_from_FACTORY_GATE_CMDS_else_the_spec()
    {
        var j = new Job(Jobs.New("J-001", "gate"));
        Files.Put(JobFile("J-001", "spec.md"), "# Spec: x\n\n## Gate\n- build: `dotnet build`\n- test: `dotnet test`\n");
        Assert.Equal([new GateCommand("build", "dotnet build"), new GateCommand("test", "dotnet test")], Sut.GateCommands(j));

        _options.GateCmds = """[{"name":"a","cmd":"exit 0"},{"name":"b","cmd":"exit 1"}]""";
        Assert.Equal([new GateCommand("a", "exit 0"), new GateCommand("b", "exit 1")], Sut.GateCommands(j));

        _options.GateCmds = """{"name":"one","cmd":"exit 0"}""";
        Assert.Equal([new GateCommand("one", "exit 0")], Sut.GateCommands(j));
    }

    [Fact]
    public void Gate_runs_each_command_with_the_lock_released_and_records_codes_sha_and_a_gate_event()
    {
        Clone();
        _git.On("branch --show-current", 0, "factory/J-001").On("rev-parse HEAD", 0, "abcdef123456");
        _options.GateCmds = """[{"name":"build","cmd":"exit 0"},{"name":"test","cmd":"exit 3"}]""";
        _shell.Run("exit 0", Wt).Returns(new ShellResult(0, "built\n"));
        _shell.Run("exit 3", Wt).Returns(new ShellResult(3, ""));
        var s = FakeSession.With(Jobs.New("J-001", "gate"));
        s.WhileUnlocked = b => b["jobs"]![0]!["flag"] = "moved meanwhile";

        Assert.Null(Sut.Gate(s, "J-001"));

        Assert.Equal(1, s.Unlocks);
        var gate = s.Job("J-001")["gate"]!.AsObject();
        Assert.Equal("""{"build":0,"test":3,"sha":"abcdef123456","at":"2026-11-10T12:00:00.000Z"}""", gate.ToJsonString());
        Assert.Equal("moved meanwhile", (string?)s.Job("J-001")["flag"]);
        Assert.Equal("build=0 test=3 sha=abcdef1", (string?)s.Events.Single()["detail"]);
        var nl = Environment.NewLine;
        Assert.Equal($"# gate J-001 at {Now}{nl}\n## build: exit 0{nl}built\n{nl}exit 0{nl}\n## test: exit 3{nl}{nl}exit 3{nl}", Files.Text(JobFile("J-001", "gate.log")));
    }

    [Fact]
    public void Gate_says_why_it_cannot_run()
    {
        var s = FakeSession.With(Jobs.New("J-001", "gate", target: "nope"));
        Assert.Equal("target 'nope' is not a GitHub repo written owner/name", Sut.Gate(s, "J-001"));

        s = FakeSession.With(Jobs.New("J-001", "gate"));
        Clone();
        _git.On("branch --show-current", 0, "factory/J-001");
        Assert.Equal("no gate commands in the spec", Sut.Gate(s, "J-001"));
        Assert.Equal(0, s.Unlocks);
    }

    [Fact]
    public void A_gate_is_green_only_when_every_code_is_0_at_the_branch_head()
    {
        var j = Jobs.New("J-001", "ready");
        Assert.False(Sut.GateGreen(new Job(j)));
        Assert.Empty(_git.Calls);

        _git.On("rev-parse --verify --quiet factory/J-001", 0, "abc");
        Assert.True(Sut.GateGreen(new Job(j.With("gate", JsonNode.Parse("""{"build":0,"sha":"abc","at":"x"}""")))));
        Assert.False(Sut.GateGreen(new Job(j.With("gate", JsonNode.Parse("""{"build":0,"sha":"old","at":"x"}""")))));
        Assert.False(Sut.GateGreen(new Job(j.With("gate", JsonNode.Parse("""{"build":2,"sha":"abc","at":"x"}""")))));
    }

    // --- merges ----------------------------------------------------------------------------------

    private static JsonObject Pr(bool merged, string head = "abc1234567") =>
        new() { ["merged"] = merged, ["merge_commit_sha"] = "m123", ["head"] = new JsonObject { ["sha"] = head } };

    [Fact]
    public void A_GitHub_merge_is_a_merged_PR_on_record()
    {
        var j = Jobs.New("J-001", "ready");
        Assert.Null(Sut.GitHubMerge(new Job(j)));

        j["pr"] = new JsonObject { ["number"] = 4, ["url"] = "u" };
        _gh.Pr("o/r", 4).Returns(Pr(false));
        Assert.Null(Sut.GitHubMerge(new Job(j)));

        _gh.Pr("o/r", 4).Returns(Pr(true));
        Assert.Equal("m123", (string?)Sut.GitHubMerge(new Job(j))!["merge_commit_sha"]);
    }

    [Fact]
    public void A_merge_records_paths_moves_to_done_deletes_branch_and_work_dir_and_marks_siblings()
    {
        Clone();
        Files.Put(Path.Combine(Wt, "a.txt"), "x");
        _git.On("diff --name-status", 0, "A\tnew.txt\nM\tREADME.md\nD\tgone.txt\nnoise");
        var s = FakeSession.With(
            Jobs.New("J-001", "ready").With("pr", new JsonObject { ["number"] = 4, ["url"] = "u4" }),
            Jobs.New("J-002", "backlog"), Jobs.New("J-003", "done"), Jobs.New("J-004", "backlog").With("idea", "I-009"));

        Assert.Empty(Sut.CompleteMerge(s, new Job(s.Job("J-001")), "m123"));

        Assert.Equal("""{"sha":"m123","repo":"o/r","pr":"u4","added":["new.txt"],"modified":["README.md"]}""", s.Job("J-001")["merged"]!.ToJsonString());
        Assert.Equal("done", (string?)s.Job("J-001")["stage"]);
        _gh.Received(1).Send(HttpMethod.Delete, "/repos/o/r/git/refs/heads/factory/J-001", null);
        Assert.Equal("-c auth fetch -q origin main", _git.Ran("fetch").Single().Args);
        Assert.Equal("-c core.quotepath=false diff --name-status --no-renames m123^1 m123", _git.Ran("diff").Single().Args);
        Assert.Equal(["stage", "merge", "note"], s.Kinds);
        Assert.Equal("base moved: J-001 merged", (string?)s.Events[2]["detail"]);
        Assert.Equal("""{"by":"J-001","sha":"m123","at":"2026-11-10T12:00:00.000Z"}""", s.Job("J-002")["baseMoved"]!.ToJsonString());
        Assert.Null(s.Job("J-003")["baseMoved"]);
        Assert.Null(s.Job("J-004")["baseMoved"]);
        Assert.False(Files.DirectoryExists(Wt));
        _launcher.DidNotReceiveWithAnyArgs().StartDetached(default!);
    }

    [Fact]
    public void A_merge_retargets_stacked_PRs_to_main_first_and_notes_each()
    {
        Clone();
        _gh.Send(HttpMethod.Patch, "/repos/o/r/pulls/8", Arg.Any<JsonNode?>()).Returns(new GitHubResponse(422, JsonNode.Parse("""{"message":"no"}"""), null));
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("idea", null),
            Jobs.New("J-002", "ready").With("base", "J-001").With("pr", new JsonObject { ["number"] = 7 }),
            Jobs.New("J-003", "ready").With("base", "J-001").With("pr", new JsonObject { ["number"] = 8 }),
            Jobs.New("J-004", "done").With("base", "J-001").With("pr", new JsonObject { ["number"] = 9 }));

        Sut.CompleteMerge(s, new Job(s.Job("J-001")), "m1");

        _gh.Received(1).Send(HttpMethod.Patch, "/repos/o/r/pulls/7", Arg.Is<JsonNode?>(b => b!.ToJsonString() == """{"base":"main"}"""));
        _gh.DidNotReceive().Send(HttpMethod.Patch, "/repos/o/r/pulls/9", Arg.Any<JsonNode?>());
        Assert.Equal(["PR #7 retargeted to main: J-001 merged", "PR #8 not retargeted to main: GitHub 422: no"],
            s.Events.Where(e => (string?)e["kind"] == "note").Select(e => (string?)e["detail"]));
    }

    [Fact]
    public void A_merged_bootstrap_releases_its_waiting_idea_and_a_preview_still_up_is_destroyed_detached()
    {
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("kind", "bootstrap").With("deploy", new JsonObject { ["status"] = "up" }));
        s.Board["ideas"]!.AsArray().Add(new JsonObject { ["id"] = "I-001", ["status"] = "waiting" });

        Assert.Equal(["released I-001"], Sut.CompleteMerge(s, new Job(s.Job("J-001")), "m1"));

        Assert.Equal("drafting", (string?)s.Board["ideas"]![0]!["status"]);
        Assert.Equal(("I-001", "idea", "drafting"), ((string?)s.Events[^1]["idea"], (string?)s.Events[^1]["kind"], (string?)s.Events[^1]["detail"]));
        _launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "destroy", "J-001" })));
        Assert.Single(_git.Ran("clone"));   // no work dir left: cloned again to read the merge
    }

    [Theory]
    [InlineData("down")]
    [InlineData("destroying")]
    public void A_preview_down_or_going_is_not_destroyed_again(string status)
    {
        Clone();
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("deploy", new JsonObject { ["status"] = status }));

        Sut.CompleteMerge(s, new Job(s.Job("J-001")), "m1");

        _launcher.DidNotReceiveWithAnyArgs().StartDetached(default!);
    }

    [Fact]
    public void A_GitHub_merge_at_a_head_the_gate_never_passed_is_noted_then_completed()
    {
        Clone();
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("pr", new JsonObject { ["number"] = 4, ["url"] = "u" })
            .With("gate", JsonNode.Parse("""{"build":0,"sha":"9999999999"}""")));

        Sut.CompleteGitHubMerge(s, new Job(s.Job("J-001")), Pr(true));

        Assert.Equal("PR #4 merged on GitHub at abc1234, not the gated 9999999", (string?)s.Events[0]["detail"]);
        Assert.Equal("m123", (string?)s.Job("J-001")["merged"]!["sha"]);

        s = FakeSession.With(Jobs.New("J-001", "ready").With("pr", new JsonObject { ["number"] = 4 }));
        Sut.CompleteGitHubMerge(s, new Job(s.Job("J-001")), Pr(true));
        Assert.Equal("PR #4 merged on GitHub at abc1234, not the gated head (no gate)", (string?)s.Events[0]["detail"]);

        s = FakeSession.With(Jobs.New("J-001", "ready").With("pr", new JsonObject { ["number"] = 4 }).With("gate", JsonNode.Parse("""{"sha":"abc1234567"}""")));
        Sut.CompleteGitHubMerge(s, new Job(s.Job("J-001")), Pr(true));
        Assert.Equal("PR #4 merged on GitHub", (string?)s.Events[0]["detail"]);
    }
}
