using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>checkout, publish, gate, gate-check, provenance, sync-prs, set-cost and next over a substituted <see cref="IJobWork"/>.</summary>
public class GitCommandsTests : CommandTestBase
{
    private readonly FakeGit _git = new();
    private readonly IJobWork _work = Substitute.For<IJobWork>();
    private readonly IGateShell _shell = Substitute.For<IGateShell>();
    private readonly FactoryOptions _options = new();
    private static readonly string Wt = Path.Combine(Work, "jobs", "J-001");

    public GitCommandsTests()
    {
        _work.Fetch(Arg.Any<string>(), Arg.Any<string[]>()).Returns(new GitResult(0, ""));
        // The helpers answer null for "no error"; a substitute would answer "".
        _work.EnsureClone(Arg.Any<JsonObject>(), Arg.Any<string>()).Returns((string?)null);
        _work.Gate(Arg.Any<IBoardSession>(), Arg.Any<string>()).Returns((string?)null);
        _work.GateCommands(Arg.Any<Job>()).Returns([new GateCommand("a", "cmd a"), new GateCommand("b", "cmd b")]);
    }

    // --- checkout --------------------------------------------------------------------------------

    private CheckoutCommand Checkout => new(Paths, _git, _work);

    [Fact]
    public void Checkout_clones_fetches_and_points_factory_base_with_the_lock_released_then_records_the_rounds_start_once()
    {
        _git.On("rev-parse HEAD", 0, "abc");
        var s = FakeSession.With(Jobs.New("J-001", "build"));

        Assert.Equal([Wt], Checkout.Run(Args("checkout", "J-001"), s).Out);

        Assert.Equal(1, s.Unlocks);
        _work.Received(1).EnsureClone(Arg.Any<JsonObject>(), "J-001");
        _work.Received(1).Fetch(Wt);
        Assert.Equal("update-ref refs/remotes/factory/base refs/remotes/origin/main", _git.Calls[0].Args);
        Assert.Equal("abc", (string?)s.Job("J-001")["roundStart"]);
        Assert.Equal(1, s.Saves);

        _git.On("rev-parse HEAD", 0, "def");
        Checkout.Run(Args("checkout", "J-001"), s);
        Assert.Equal("abc", (string?)s.Job("J-001")["roundStart"]);
        Assert.Equal(1, s.Saves);
    }

    [Fact]
    public void Checkout_outside_build_records_no_round_start()
    {
        var s = FakeSession.With(Jobs.New("J-001", "review"));

        Checkout.Run(Args("checkout", "J-001"), s);

        Assert.Null(s.Job("J-001")["roundStart"]);
        Assert.Equal(0, s.Saves);
    }

    [Fact]
    public void Checkout_refuses_with_what_went_wrong_and_writes_nothing()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build"));
        _work.EnsureClone(Arg.Any<JsonObject>(), "J-001").Returns("clone of o/r failed: x");
        Assert.Equal("clone of o/r failed: x", Assert.Throws<FactoryException>(() => Checkout.Run(Args("checkout", "J-001"), s)).Message);

        _work.EnsureClone(Arg.Any<JsonObject>(), "J-001").Returns((string?)null);
        _work.Fetch(Wt).Returns(new GitResult(1, "no remote"));
        Assert.Equal("checkout: fetch failed: no remote", Assert.Throws<FactoryException>(() => Checkout.Run(Args("checkout", "J-001"), s)).Message);

        _work.Fetch(Wt).Returns(new GitResult(0, ""));
        _git.On("update-ref", 1, "bad ref");
        Assert.Equal("checkout: cannot set factory/base: bad ref", Assert.Throws<FactoryException>(() => Checkout.Run(Args("checkout", "J-001"), s)).Message);
        Assert.Equal(0, s.Saves);
    }

    // --- publish ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("ready")]
    [InlineData("blocked")]
    public void Publish_prints_the_PR_url(string stage)
    {
        _work.Publish(Arg.Any<IBoardSession>(), Arg.Any<Job>()).Returns(("u4", (string?)null));

        Assert.Equal(["u4"], new PublishCommand(_work).Run(Args("publish", "J-001"), FakeSession.With(Jobs.New("J-001", stage))).Out);
    }

    [Fact]
    public void Publish_refuses_outside_ready_or_blocked_and_says_why_it_failed()
    {
        var sut = new PublishCommand(_work);
        Assert.Equal("publish refused: J-001 is in 'build', needs ready|blocked",
            Assert.Throws<FactoryException>(() => sut.Run(Args("publish", "J-001"), FakeSession.With(Jobs.New("J-001", "build")))).Message);

        _work.Publish(Arg.Any<IBoardSession>(), Arg.Any<Job>()).Returns(((string?)null, "push failed: x"));
        Assert.Equal("publish refused: push failed: x",
            Assert.Throws<FactoryException>(() => sut.Run(Args("publish", "J-001"), FakeSession.With(Jobs.New("J-001", "ready")))).Message);
    }

    // --- gate ------------------------------------------------------------------------------------

    private GateJobCommand Gate => new(_work, Clock);

    [Fact]
    public void A_green_gate_lands_in_ready_unflagged()
    {
        _work.GateGreen(Arg.Any<Job>()).Returns(true);
        var s = FakeSession.With(Jobs.New("J-001", "gate"));

        Assert.Equal(0, Gate.Run(Args("gate", "J-001"), s).ExitCode);

        Assert.Equal(("ready", null, "gate"), ((string?)s.Job("J-001")["stage"], s.Job("J-001")["flag"], (string?)s.Events.Single()["by"]));
    }

    [Fact]
    public void A_red_gate_lands_in_ready_flagged_gate_failed_and_exits_1()
    {
        var s = FakeSession.With(Jobs.New("J-001", "gate"));

        var r = Gate.Run(Args("gate", "J-001"), s);

        Assert.Equal((1, 0, 0), (r.ExitCode, r.Out.Count, r.Err.Count));
        Assert.Equal("gate-failed", (string?)s.Job("J-001")["flag"]!["reason"]);
    }

    [Fact]
    public void A_gate_that_cannot_run_is_escalated_and_refused_and_one_outside_gate_is_refused()
    {
        _work.Gate(Arg.Any<IBoardSession>(), "J-001").Returns("no gate commands in the spec");
        var s = FakeSession.With(Jobs.New("J-001", "gate"));

        Assert.Equal("no gate commands in the spec", Assert.Throws<FactoryException>(() => Gate.Run(Args("gate", "J-001"), s)).Message);
        Assert.Equal(("ready", "escalated"), ((string?)s.Job("J-001")["stage"], (string?)s.Job("J-001")["flag"]!["reason"]));

        Assert.Equal("gate refused: J-001 is in 'ready', needs gate", Assert.Throws<FactoryException>(() => Gate.Run(Args("gate", "J-001"), s)).Message);
    }

    // --- gate-check ------------------------------------------------------------------------------

    private GateCheckCommand GateCheck => new(Paths, Files, _git, _shell, _work, Options.Create(_options), Clock);

    private void Head(string sha, bool clean = true) => _git.On("rev-parse HEAD", 0, sha).On("status --porcelain", 0, clean ? "" : "?? x");

    [Fact]
    public void Gate_check_runs_every_command_prints_them_and_exits_with_the_first_failure_leaving_the_board_alone()
    {
        Head("abc1234567");
        _shell.Run(new GateCommand("a", "cmd a"), Wt).Returns(new ShellResult(3, "ran a\n"));
        _shell.Run(new GateCommand("b", "cmd b"), Wt).Returns(new ShellResult(5, "ran b\n"));
        var s = FakeSession.With(Jobs.New("J-001", "review"));

        var r = GateCheck.Run(Args("gate-check", "J-001"), s);

        Assert.Equal(3, r.ExitCode);
        Assert.Equal([$"# gate-check J-001 at {Now}", "\n## a: cmd a", "ran a", "exit 3", "\n## b: cmd b", "ran b", "exit 5"], r.Out);
        Assert.Equal((0, 0, 1), (s.Saves, s.Events.Count, s.Unlocks));
        Assert.Equal(string.Concat(r.Out.Select(l => l + Environment.NewLine)), Files.Text(JobFile("J-001", "gate-check.log")));
        Assert.Empty(Files.Held);
    }

    [Fact]
    public void Gate_check_runs_only_the_named_command_and_refuses_an_unknown_name()
    {
        Head("abc");
        _shell.Run(Arg.Any<GateCommand>(), Arg.Any<string>()).Returns(new ShellResult(0, ""));

        GateCheck.Run(Args("gate-check", "J-001", "B"), FakeSession.With(Jobs.New("J-001", "build")));
        _shell.Received(1).Run(new GateCommand("b", "cmd b"), Wt);
        _shell.DidNotReceive().Run(new GateCommand("a", "cmd a"), Arg.Any<string>());

        Assert.Equal("gate-check refused: no gate command named 'nope'",
            Assert.Throws<FactoryException>(() => GateCheck.Run(Args("gate-check", "J-001", "nope"), FakeSession.With(Jobs.New("J-001", "build")))).Message);
        _work.GateCommands(Arg.Any<Job>()).Returns([]);
        Assert.Equal("gate-check refused: no gate commands in the spec",
            Assert.Throws<FactoryException>(() => GateCheck.Run(Args("gate-check", "J-001"), FakeSession.With(Jobs.New("J-001", "build")))).Message);
    }

    [Fact]
    public void Gate_check_serves_a_clean_heads_results_from_its_cache_and_runs_again_on_a_new_head()
    {
        Head("abc1234567");
        _shell.Run(new GateCommand("a", "cmd a"), Wt).Returns(new ShellResult(0, "ran a\n"));
        _shell.Run(new GateCommand("b", "cmd b"), Wt).Returns(new ShellResult(4, "ran b\n"));
        GateCheck.Run(Args("gate-check", "J-001"), FakeSession.With(Jobs.New("J-001", "build")));

        var r = GateCheck.Run(Args("gate-check", "J-001"), FakeSession.With(Jobs.New("J-001", "build")));

        Assert.Equal(4, r.ExitCode);
        Assert.Contains("exit 4 (cached at abc1234)", r.Out);
        Assert.Contains("ran b", r.Out);
        _shell.Received(1).Run(new GateCommand("b", "cmd b"), Wt);

        Head("def");
        GateCheck.Run(Args("gate-check", "J-001", "a"), FakeSession.With(Jobs.New("J-001", "build")));
        _shell.Received(2).Run(new GateCommand("a", "cmd a"), Wt);
    }

    [Fact]
    public void Gate_check_does_not_cache_a_dirty_tree()
    {
        Head("abc", clean: false);
        _shell.Run(Arg.Any<GateCommand>(), Arg.Any<string>()).Returns(new ShellResult(0, ""));

        GateCheck.Run(Args("gate-check", "J-001", "a"), FakeSession.With(Jobs.New("J-001", "build")));
        var r = GateCheck.Run(Args("gate-check", "J-001", "a"), FakeSession.With(Jobs.New("J-001", "build")));

        Assert.DoesNotContain(r.Out, l => l.Contains("cached at"));
        _shell.Received(2).Run(new GateCommand("a", "cmd a"), Wt);
        Assert.Null(Files.Text(JobFile("J-001", "gate-check.json")));
    }

    [Fact]
    public void Gate_check_does_not_serve_the_cache_once_the_tree_is_dirty_at_the_same_head()
    {
        Head("abc");
        _shell.Run(Arg.Any<GateCommand>(), Arg.Any<string>()).Returns(new ShellResult(0, ""));
        GateCheck.Run(Args("gate-check", "J-001", "a"), FakeSession.With(Jobs.New("J-001", "build")));

        Head("abc", clean: false);
        var r = GateCheck.Run(Args("gate-check", "J-001", "a"), FakeSession.With(Jobs.New("J-001", "build")));

        Assert.DoesNotContain(r.Out, l => l.Contains("cached at"));
        _shell.Received(2).Run(new GateCommand("a", "cmd a"), Wt);
    }

    [Fact]
    public void Gate_check_starts_afresh_from_an_unreadable_cache()
    {
        Head("abc");
        Files.Put(JobFile("J-001", "gate-check.json"), "{not json");
        _shell.Run(Arg.Any<GateCommand>(), Arg.Any<string>()).Returns(new ShellResult(0, ""));

        Assert.Equal(0, GateCheck.Run(Args("gate-check", "J-001", "a"), FakeSession.With(Jobs.New("J-001", "build"))).ExitCode);
        Assert.Contains("\"sha\": \"abc\"", Files.Text(JobFile("J-001", "gate-check.json")));
    }

    [Fact]
    public void Gate_check_waits_for_anothers_lock_and_refuses_after_FACTORY_GATE_CHECK_WAIT_MINUTES()
    {
        _options.GateCheckWaitMinutes = "-1";
        Files.Held.Add(JobFile("J-001", "gate-check.lock"));

        Assert.Equal("gate-check refused: another gate-check held J-001's lock for too long",
            Assert.Throws<FactoryException>(() => GateCheck.Run(Args("gate-check", "J-001"), FakeSession.With(Jobs.New("J-001", "build")))).Message);
        _shell.DidNotReceiveWithAnyArgs().Run(default!, default!);
    }

    [Fact]
    public void Gate_check_refuses_a_job_it_cannot_check()
    {
        Assert.Equal("gate-check refused: J-001 is in 'ready', needs build|review|deploy|approve",
            Assert.Throws<FactoryException>(() => GateCheck.Run(Args("gate-check", "J-001"), FakeSession.With(Jobs.New("J-001", "ready")))).Message);

        _work.EnsureClone(Arg.Any<JsonObject>(), "J-001").Returns("o/r has no main branch");
        Assert.Equal("gate-check refused: o/r has no main branch",
            Assert.Throws<FactoryException>(() => GateCheck.Run(Args("gate-check", "J-001"), FakeSession.With(Jobs.New("J-001", "build")))).Message);
    }

    // --- provenance, sync-prs, set-cost, next ----------------------------------------------------

    [Fact]
    public void Provenance_prints_the_paths_and_the_note_on_stderr()
    {
        var p = Substitute.For<ITemplateProvenance>();
        p.For(Arg.Any<JsonObject>(), Arg.Any<Job>()).Returns(new Provenance(["src/a.ts"], "no bootstrap merge on record"));

        var r = new ProvenanceCommand(p).Run(Args("provenance", "J-001"), FakeSession.With(Jobs.New("J-001", "build")));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["src/a.ts"], r.Out);
        Assert.Equal(["factory: note: no bootstrap merge on record"], r.Err);

        p.For(Arg.Any<JsonObject>(), Arg.Any<Job>()).Returns(new Provenance(null, null));
        r = new ProvenanceCommand(p).Run(Args("provenance", "J-001"), FakeSession.With(Jobs.New("J-001", "build")));
        Assert.Equal((0, 0), (r.Out.Count, r.Err.Count));
    }

    [Fact]
    public void Sync_prs_completes_each_ready_job_merged_on_GitHub_and_prints_its_id()
    {
        var merged = new JsonObject { ["merged"] = true };
        var pr = new JsonObject { ["number"] = 1 };
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("pr", pr.DeepClone()), Jobs.New("J-002", "ready").With("pr", pr.DeepClone()),
            Jobs.New("J-003", "ready"), Jobs.New("J-004", "build").With("pr", pr.DeepClone()));
        _work.GitHubMerge(Arg.Is<Job>(j => j.Id == "J-001")).Returns(merged);
        _work.CompleteGitHubMerge(s, Arg.Is<Job>(j => j.Id == "J-001"), merged).Returns(["released I-001"]);

        Assert.Equal(["J-001", "released I-001"], new SyncPrsCommand(_work).Run(Args("sync-prs"), s).Out);
        _work.Received(2).GitHubMerge(Arg.Any<Job>());
    }

    private SetCostCommand SetCost => new(DotEnv, Options.Create(_options), Clock);

    [Fact]
    public void Set_cost_accumulates_rounded_cost_and_duration_and_appends_a_cost_event()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build"));

        SetCost.Run(Args("set-cost", "J-001", "0.1", "100"), s);
        SetCost.Run(Args("set-cost", "J-001", "0.2", "50.5"), s);
        SetCost.Run(Args("set-cost", "J-001", "1", ""), s);

        Assert.Equal(("1.3", "150"), (s.Job("J-001")["costUsd"]!.ToJsonString(), s.Job("J-001")["durationMs"]!.ToJsonString()));
        Assert.Equal("""{"ts":"2026-11-10T12:00:00.000Z","job":"J-001","kind":"cost","costUsd":1.0,"durationMs":0,"provider":"subscription"}""",
            PsJson.WriteCompact(s.Events[2]));
        Assert.Equal(3, s.Saves);
    }

    [Fact]
    public void Set_cost_rounds_the_total_to_four_places_half_to_even()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build"));

        SetCost.Run(Args("set-cost", "J-001", "0.123456", "1"), s);
        SetCost.Run(Args("set-cost", "J-001", "0.00005", "1"), s);

        Assert.Equal("0.1236", s.Job("J-001")["costUsd"]!.ToJsonString());
        Assert.Equal("5E-05", s.Events[1]["costUsd"]!.ToJsonString());
    }

    [Fact]
    public void Set_cost_takes_the_run_mode_from_the_environment_first_then_env_file()
    {
        DotEnv.Get("FACTORY_MODE").Returns("cloud");
        var s = FakeSession.With(Jobs.New("J-001", "build"));
        SetCost.Run(Args("set-cost", "J-001", "0.1", "1"), s);
        _options.Mode = "local";
        SetCost.Run(Args("set-cost", "J-001", "0.1", "1"), s);

        Assert.Equal(["foundry", "subscription"], s.Events.Select(e => (string?)e["provider"]));
    }

    [Fact]
    public void Set_cost_refuses_numbers_it_cannot_read()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build"));
        Assert.Throws<FactoryException>(() => SetCost.Run(Args("set-cost", "J-001", "x", "1"), s));
        Assert.Throws<FactoryException>(() => SetCost.Run(Args("set-cost", "J-001", "1", "x"), s));
        Assert.Equal(0, s.Saves);
    }

    [Fact]
    public void Next_prints_the_queues_head_or_canary_or_nothing()
    {
        var canary = Substitute.For<ICanary>();
        canary.State().Returns(CanaryState.None);
        var sut = new NextCommand(DotEnv, canary);
        var s = FakeSession.With(Jobs.New("J-001", "approved"));

        Assert.Equal(["J-001"], sut.Run(Args("next"), s).Out);

        DotEnv.Get("HCP_ORG").Returns("org");
        Assert.Empty(sut.Run(Args("next"), s).Out);
        Assert.Equal(["canary"], sut.Run(Args("next", "-NeedsCanary"), s).Out);
    }
}
