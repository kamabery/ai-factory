using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class SetStageCommandTests : CommandTestBase
{
    private readonly IGitClient _git = Substitute.For<IGitClient>();
    private readonly ICanary _canary = Substitute.For<ICanary>();
    private readonly ITemplateProvenance _provenance = Substitute.For<ITemplateProvenance>();

    private SetStageCommand Sut => new(Paths, Files, _git, DotEnv, _canary, _provenance, Clock);

    private static JsonObject Approved(string id, string at = "2026-01-01T00:00:00.000Z") => Jobs.New(id, "approved").With("approvedAt", at);

    private void Clone(string id) => Files.Directories.Add(Path.Combine(Work, "jobs", id, ".git"));

    private void Diff(params string[] paths)
    {
        _git.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a.Contains("rev-parse"))).Returns(new GitResult(1, ""));
        _git.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a.Contains("diff"))).Returns(new GitResult(0, string.Join("\n", paths)));
    }

    // --- the queue ---------------------------------------------------------------------------

    [Fact]
    public void Approved_to_build_takes_the_queues_head_and_resets_its_reviews()
    {
        var s = FakeSession.With(Approved("J-001").With("reviews", new JsonObject { ["code"] = "CHANGES" }), Approved("J-002", "2026-02-01T00:00:00.000Z"));

        var r = Sut.Run(Args("set-stage", "J-001", "build", "-By", "runner"), s);

        Assert.Empty(r.Out);
        Assert.Equal(("build", "pending", "runner"), ((string?)s.Job("J-001")["stage"], (string?)s.Job("J-001")["reviews"]!["code"], (string?)s.Events.Single()["by"]));
        _canary.DidNotReceive().State();
    }

    [Fact]
    public void Approved_to_build_refuses_a_job_behind_the_head_or_while_one_builds()
    {
        var s = FakeSession.With(Approved("J-001"), Approved("J-002", "2026-02-01T00:00:00.000Z"));

        var e = Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-002", "BUILD"), s));

        Assert.Equal("approved->build refused: J-002 is not next in the queue, or a job is already building", e.Message);
        Assert.Equal(0, s.Saves);
    }

    [Theory]
    [InlineData(CanaryState.Pass, true)]
    [InlineData(CanaryState.Failed, false)]
    [InlineData(CanaryState.None, false)]
    public void A_job_waiting_in_canary_goes_to_build_only_after_a_pass(CanaryState state, bool moves)
    {
        DotEnv.Get("HCP_ORG").Returns("org");
        _canary.State().Returns(state);
        var s = FakeSession.With(Approved("J-001").With("stage", "canary"));

        if (moves) Sut.Run(Args("set-stage", "J-001", "build", "-By", "runner"), s);
        else Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "build", "-By", "runner"), s));

        Assert.Equal(moves ? "build" : "canary", (string?)s.Job("J-001")["stage"]);
    }

    [Fact]
    public void A_deployable_head_held_for_the_canary_lets_a_bootstrap_behind_it_through()
    {
        DotEnv.Get("HCP_ORG").Returns("org");
        _canary.State().Returns(CanaryState.None);
        var s = FakeSession.With(Approved("J-001"), Approved("J-002", "2026-02-01T00:00:00.000Z").With("kind", "bootstrap"));

        Sut.Run(Args("set-stage", "J-002", "build"), s);

        Assert.Equal("build", (string?)s.Job("J-002")["stage"]);
        _canary.Received(1).State();
    }

    [Fact]
    public void A_redeployed_job_goes_to_deploy_not_build()
    {
        var s = FakeSession.With(Approved("J-001").With("resume", "deploy"));

        Assert.Equal("approved->build refused: J-001 was redeployed; it resumes at deploy",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "build"), s)).Message);
        Sut.Run(Args("set-stage", "J-001", "deploy"), s);

        Assert.Equal("deploy", (string?)s.Job("J-001")["stage"]);
        Assert.Null(s.Job("J-001")["resume"]);
        Assert.Equal("approved->deploy refused: J-002 was not redeployed",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-002", "deploy"), FakeSession.With(Approved("J-002")))).Message);
    }

    [Fact]
    public void A_resumed_job_goes_back_to_the_stage_it_stopped_in_keeping_its_round_start()
    {
        var s = FakeSession.With(Approved("J-001").With("resume", "build").With("resumeSession", "sess").With("roundStart", "abc123"));

        Assert.Equal("approved->review refused: J-001 was resumed; it resumes at build",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "review"), s)).Message);
        Assert.Empty(Sut.Run(Args("set-stage", "J-001", "build"), s).Out);

        var j = s.Job("J-001");
        Assert.Equal(("build", "abc123"), ((string?)j["stage"], (string?)j["roundStart"]));
        Assert.Null(j["resume"]);
        Assert.Null(j["resumeSession"]);
    }

    [Fact]
    public void A_job_resumed_in_review_goes_back_to_review_with_its_reviews_as_they_were()
    {
        var reviews = new JsonObject { ["security"] = "pass", ["code"] = "running", ["ux"] = "pending", ["ui"] = "pass" };
        var s = FakeSession.With(Approved("J-001").With("round", 2).With("resume", "review").With("resumeSession", "sess")
            .With("roundStart", "abc123").With("reviews", reviews));

        Assert.Equal("approved->build refused: J-001 was resumed; it resumes at review",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "build"), s)).Message);
        Sut.Run(Args("set-stage", "J-001", "review"), s);

        var j = s.Job("J-001");
        Assert.Equal(("review", 2, "abc123"), ((string?)j["stage"], (int)j["round"]!, (string?)j["roundStart"]));
        Assert.Equal("""{"security":"pass","code":"running","ux":"pending","ui":"pass"}""", j["reviews"]!.ToJsonString());
    }

    // --- build -> review ---------------------------------------------------------------------

    [Fact]
    public void Build_to_review_needs_the_rounds_build_md()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build"));

        Assert.Equal("build->review refused: round-1/build.md missing", Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "review"), s)).Message);
    }

    [Fact]
    public void Build_to_review_without_scope_globs_starts_the_reviewers_and_notes_it_was_not_scope_checked()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build"));
        Files.Put(JobFile("J-001", "round-1/build.md"), "# Build");
        Files.Put(JobFile("J-001", "spec.md"), "# Spec\n");

        var r = Sut.Run(Args("set-stage", "J-001", "review"), s);

        Assert.Equal(["review"], r.Out);
        Assert.Equal("running", (string?)s.Job("J-001")["reviews"]!["ui"]);
        Assert.Equal(["stage", "note"], s.Kinds);
        Assert.Equal("not scope-checked: the spec has no Scope: In globs", (string?)s.Events[1]["detail"]);
    }

    [Fact]
    public void Build_to_review_with_every_path_in_scope_starts_the_reviewers()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build"));
        Files.Put(JobFile("J-001", "round-1/build.md"), "# Build");
        // ** crosses segments, and a sub-bullet without a backticked glob is prose, not a glob.
        Files.Put(JobFile("J-001", "spec.md"), "## Scope\n- In:\n  - `src/**`\n  - `docs/*.md` - the docs\n  - and nothing else\n");
        Clone("J-001");
        Diff("src/a/b/c.cs", "docs/readme.md");

        var r = Sut.Run(Args("set-stage", "J-001", "review"), s);

        Assert.Equal(["review"], r.Out);
        Assert.Null(s.Job("J-001")["flag"]);
        Assert.Equal(["stage"], s.Kinds);
    }

    [Fact]
    public void Build_to_review_with_paths_outside_scope_lands_a_job_that_is_not_deployable_in_ready()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build"));
        Files.Put(JobFile("J-001", "round-1/build.md"), "# Build");
        Files.Put(JobFile("J-001", "spec.md"), "## Scope\n- In:\n  - `src/**`\n");
        Clone("J-001");
        Diff("src/a.cs", "README.md");

        var r = Sut.Run(Args("set-stage", "J-001", "review"), s);

        Assert.Equal(["ready"], r.Out);
        Assert.Equal(("out-of-scope", "Paths outside the spec's Scope: In:\n- `README.md`"),
            ((string?)s.Job("J-001")["flag"]!["reason"], (string?)s.Job("J-001")["flag"]!["why"]));
    }

    [Fact]
    public void Build_to_review_with_paths_outside_scope_blocks_a_deployable_job_listing_them()
    {
        DotEnv.Get("HCP_ORG").Returns("org");
        var s = FakeSession.With(Jobs.New("J-001", "build"));
        Files.Put(JobFile("J-001", "round-1/build.md"), "# Build");
        Files.Put(JobFile("J-001", "spec.md"), "## Scope\n- In:\n  - `src/**`\n");
        Clone("J-001");
        Diff("src/a.cs", "docs/a.md", "README.md", "x/y", "z");

        var r = Sut.Run(Args("set-stage", "J-001", "review"), s);

        Assert.Equal(["blocked"], r.Out);
        var flag = s.Job("J-001")["flag"]!;
        Assert.Equal(("out-of-scope", "4 paths outside Scope: In: docs/a.md, README.md, x/y, ..."), ((string?)flag["reason"], (string?)flag["detail"]));
        Assert.Equal("Paths outside the spec's Scope: In:\n- `docs/a.md`\n- `README.md`\n- `x/y`\n- `z`", (string?)flag["why"]);
        _git.Received().Run(Path.Combine(Work, "jobs", "J-001"), "-c", "core.quotePath=false", "diff", "--name-only", "origin/main...HEAD");
    }

    [Fact]
    public void Build_to_review_with_globs_needs_a_work_dir_and_a_diff()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build"));
        Files.Put(JobFile("J-001", "round-1/build.md"), "# Build");
        Files.Put(JobFile("J-001", "spec.md"), "## Scope\n- In:\n  - `src/**`\n");

        Assert.Equal("build->review refused: J-001 has no work dir to scope-check", Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "review"), s)).Message);

        Clone("J-001");
        _git.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a.Contains("rev-parse"))).Returns(new GitResult(0, "sha"));
        _git.Run(Arg.Any<string>(), Arg.Is<string[]>(a => a.Contains("diff"))).Returns(new GitResult(128, "fatal: bad revision"));
        Assert.Equal("build->review refused: cannot diff factory/base...HEAD: fatal: bad revision", Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "review"), s)).Message);
    }

    [Theory]
    [InlineData("## Spec against base\nSpec holds: yes\n", "review", null)]
    [InlineData("## Spec against base\nSpec holds: no, the API changed\n", "ready", "spec-stale")]
    public void Build_to_review_after_the_base_moved_needs_the_builders_word_on_the_spec(string buildMd, string stage, string? reason)
    {
        var s = FakeSession.With(Jobs.New("J-001", "build").With("baseMoved", new JsonObject { ["by"] = "J-002" }));
        Files.Put(JobFile("J-001", "round-1/build.md"), "# Build\n\n" + buildMd);
        Files.Put(JobFile("J-001", "spec.md"), "# Spec\n");

        Assert.Equal([stage], Sut.Run(Args("set-stage", "J-001", "review"), s).Out);

        Assert.Equal(reason, (string?)s.Job("J-001")["flag"]?["reason"]);
        if (reason is null) Assert.Null(s.Job("J-001")["baseMoved"]);
        else Assert.Equal(("the spec does not hold since J-002 merged", "## Spec against base\nSpec holds: no, the API changed"),
            ((string?)s.Job("J-001")["flag"]!["detail"], (string?)s.Job("J-001")["flag"]!["why"]));
    }

    [Fact]
    public void Build_to_review_after_the_base_moved_without_the_section_exits_2()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build").With("baseMoved", new JsonObject { ["by"] = "J-002" }));
        Files.Put(JobFile("J-001", "round-1/build.md"), "# Build\n");

        var e = Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "review"), s));

        Assert.Equal(("build->review refused: J-001's base moved (J-002 merged), and round-1/build.md has no '## Spec against base' with a 'Spec holds: yes|no' line", 2), (e.Message, e.ExitCode));
    }

    [Fact]
    public void Build_to_review_after_round_1_needs_template_paths_named_in_template_gaps()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build", round: 2));
        Files.Put(JobFile("J-001", "round-2/build.md"), "# Build\n\n## Template gaps\n- `IaC/main.tf`: job-specific, a new output\n");
        _provenance.For(Arg.Any<JsonObject>(), Arg.Any<Job>()).Returns(new Provenance(["IaC/main.tf", "Program.cs"], null));

        var e = Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "review"), s));

        Assert.Equal(("build->review refused: round 2 changed template-sourced paths that build.md's '## Template gaps' does not name (as a gap, or '<path>: job-specific, <why>'): Program.cs", 2), (e.Message, e.ExitCode));
    }

    [Fact]
    public void Build_to_review_with_no_round_start_notes_it_was_not_provenance_checked()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build", round: 2));
        Files.Put(JobFile("J-001", "round-2/build.md"), "# Build\n");
        _provenance.For(Arg.Any<JsonObject>(), Arg.Any<Job>()).Returns(new Provenance(null, "no round start recorded for J-001"));

        Sut.Run(Args("set-stage", "J-001", "review"), s);

        Assert.Equal(["note", "stage", "note"], s.Kinds);
        Assert.Equal("not provenance-checked: no round start recorded for J-001", (string?)s.Events[0]["detail"]);
    }

    // --- escalation and refusals ---------------------------------------------------------------

    [Fact]
    public void A_usage_limit_parks_the_job_with_the_stage_and_session_to_resume()
    {
        var s = FakeSession.With(Jobs.New("J-001", "review"));

        Assert.Equal("usage-limit refused: J-001 needs -Session, the claude session to resume",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("set-stage", "J-001", "ready", "-Reason", "usage-limit"), s)).Message);
        Sut.Run(Args("set-stage", "J-001", "ready", "-Reason", "usage-limit", "-Detail", "limit until 5pm", "-Session", "s-1"), s);

        Assert.Equal("""{"reason":"usage-limit","detail":"limit until 5pm","stage":"review","session":"s-1"}""", s.Job("J-001")["flag"]!.ToJsonString());
    }

    [Fact]
    public void The_orchestrator_escalates_a_building_job_to_ready_or_blocked()
    {
        DotEnv.Get("HCP_ORG").Returns("org");
        var s = FakeSession.With(Jobs.New("J-001", "build"), Jobs.New("J-002", "gate"));

        Sut.Run(Args("set-stage", "J-001", "ready", "-Reason", "escalated", "-Detail", "stuck"), s);
        Sut.Run(Args("set-stage", "J-002", "ready", "-Reason", "escalated"), s);

        Assert.Equal(("blocked", "ready", ""), ((string?)s.Job("J-001")["stage"], (string?)s.Job("J-002")["stage"], (string?)s.Job("J-002")["flag"]!["detail"]));
    }

    [Theory]
    [InlineData("backlog", "build", null)]
    [InlineData("review", "ready", "max-rounds")]
    [InlineData("approved", "review", null)]
    public void Any_other_move_is_refused(string from, string to, string? reason)
    {
        var s = FakeSession.With(Jobs.New("J-001", from));
        string[] args = reason is null ? ["set-stage", "J-001", to] : ["set-stage", "J-001", to, "-Reason", reason];

        Assert.Equal($"transition {from}->{to} refused (use the dedicated command, or see FACTORY-CONTRACT.md)",
            Assert.Throws<FactoryException>(() => Sut.Run(Args(args), s)).Message);
    }
}
