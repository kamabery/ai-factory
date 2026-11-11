using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>eval, feedback, lesson and lesson-mark, and the state admin commands (spec 11).</summary>
public class EvalLessonCommandsTests : CommandTestBase
{
    private static readonly string Cwd = Path.Combine(Path.GetTempPath(), "cmd-cwd");
    private readonly FactoryOptions _options = new() { WorkingDirectory = Cwd };

    private string Evals(string rel) => Path.Combine(Home, "evals", rel);
    private string Lessons(string rel) => Path.Combine(Home, "lessons", rel);

    private string EvalFile(string text = "Rating: mixed\n\n# Eval") { Files.Put(Path.Combine(Cwd, "eval.md"), text); return "eval.md"; }

    private JsonObject Json(string path) => JsonNode.Parse(Files.Text(path)!)!.AsObject();

    private void Event(string json) => Files.AppendText(Path.Combine(Home, "events.jsonl"), json + "\n");

    // --- eval ------------------------------------------------------------------------------------

    private EvalCommand Eval => new(Paths, Files, Options.Create(_options), Clock);

    [Fact]
    public void Eval_snapshots_the_job_with_models_agents_and_gate_time_and_appends_an_eval_event()
    {
        var job = Jobs.New("J-001", "done").With("gate", JsonNode.Parse("""{"build":0,"sha":"s","at":"a"}""")).With("merged", new JsonObject { ["sha"] = "m1" })
            .With("flag", new JsonObject { ["reason"] = "escalated" }).With("costUsd", JsonNode.Parse("1.5")).With("kind", null);
        Files.Put(JobFile("J-001", "run-1.jsonl"), """
            {"type":"result","modelUsage":{"opus":{"costUSD":0.1,"outputTokens":10}}}
            {"type":"result","modelUsage":{"opus":{"costUSD":0.5,"outputTokens":50},"haiku":{"costUSD":0.25}}}
            """);
        Files.Put(JobFile("J-001", "run-2.jsonl"), "{\"type\":\"result\",\"modelUsage\":{\"opus\":{\"costUSD\":1,\"outputTokens\":5}}}\nnot json\n");
        Files.Put(JobFile("J-001", "run-3.jsonl"), "{\"type\":\"result\"}\n");
        Files.Put(JobFile("J-001", "run-4.jsonl"), "{\"type\":\"result\",\"modelUsage\":{\"sonnet\":{\"costUSD\":0.123456}}}\n");
        Event("""{"ts":"2026-11-10T10:00:00.000Z","job":"J-001","kind":"agent-start","agent":"builder","round":1}""");
        Event("""{"ts":"2026-11-10T10:00:02.500Z","job":"J-001","kind":"agent-stop","agent":"builder","round":1}""");
        Event("""{"ts":"2026-11-10T10:01:00.000Z","job":"J-001","kind":"agent-start","agent":"builder","round":1}""");
        Event("""{"ts":"2026-11-10T10:01:01.000Z","job":"J-001","kind":"agent-stop","agent":"builder","round":1}""");
        Event("""{"ts":"2026-11-10T10:02:00.000Z","job":"J-001","kind":"agent-stop","agent":"ux","round":1}""");
        Event("""{"ts":"2026-11-10T10:03:00.000Z","job":"J-002","kind":"agent-start","agent":"builder","round":1}""");
        Event("""{"ts":"2026-11-10T10:04:00.000Z","job":"J-001","kind":"stage","to":"gate"}""");
        Event("""{"ts":"2026-11-10T10:04:30.000Z","job":"J-001","kind":"gate"}""");
        Event("""{"ts":"2026-11-10T10:05:00.000Z","job":"J-001","kind":"gate"}""");
        Event("broken");
        var s = FakeSession.With(job);

        Eval.Run(Args("eval", "J-001", "-File", EvalFile()), s);

        var snap = Json(Evals("J-001.json"));
        Assert.Equal(
            """{"job":"J-001","idea":"I-001","title":"Thing","kind":"feature","target":"o/r","rating":"mixed","stage":"done","round":1,"flag":"escalated","reviews":{"security":"pending","code":"pending","ux":"pending","ui":"pending"},"decision":null,"gate":"green","merged":"m1","costUsd":1.5,"durationMs":null,"evaluatedAt":"2026-11-10T12:00:00.000Z","models":{"opus":{"costUsd":1.5,"outputTokens":55},"haiku":{"costUsd":0.25,"outputTokens":0},"sonnet":{"costUsd":0.1235,"outputTokens":0}},"agents":[{"agent":"builder","round":1,"ms":3500}],"gateMs":30000}""",
            snap.ToJsonString());
        Assert.Equal("Rating: mixed\n\n# Eval", Files.Text(Evals("J-001.md")));
        Assert.Equal(("eval", "mixed"), ((string?)s.Events.Single()["kind"], (string?)s.Events.Single()["detail"]));
    }

    [Fact]
    public void Eval_keeps_a_human_rating_given_before_and_calls_a_gate_with_a_failure_red()
    {
        Files.Put(Evals("J-001.json"), """{"human":{"rating":"good","at":"x"}}""");
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("gate", JsonNode.Parse("""{"build":0,"test":2,"sha":"s"}""")));

        Eval.Run(Args("eval", "J-001", "-File", EvalFile("Rating: poor")), s);

        var snap = Json(Evals("J-001.json"));
        Assert.Equal(("red", "good"), ((string?)snap["gate"], (string?)snap["human"]!["rating"]));
        Assert.Equal("{}", snap["models"]!.ToJsonString());
        Assert.Equal(("[]", null), (snap["agents"]!.ToJsonString(), snap["gateMs"]));
    }

    [Fact]
    public void Eval_refuses_an_unfinished_job_a_missing_file_and_a_first_line_that_is_no_rating()
    {
        Assert.Equal("eval refused: J-001 is in 'build', needs ready|done|discarded",
            Assert.Throws<FactoryException>(() => Eval.Run(Args("eval", "J-001", "-File", EvalFile()), FakeSession.With(Jobs.New("J-001", "build")))).Message);
        Assert.Equal("eval file not found: nope.md",
            Assert.Throws<FactoryException>(() => Eval.Run(Args("eval", "J-001", "-File", "nope.md"), FakeSession.With(Jobs.New("J-001", "done")))).Message);
        Assert.Equal("eval file not found: ",
            Assert.Throws<FactoryException>(() => Eval.Run(Args("eval", "J-001"), FakeSession.With(Jobs.New("J-001", "done")))).Message);
        var e = Assert.Throws<FactoryException>(() => Eval.Run(Args("eval", "J-001", "-File", EvalFile("rating: Good")), FakeSession.With(Jobs.New("J-001", "done"))));
        Assert.Equal(("no 'Rating: good|mixed|poor' first line in eval.md", 2), (e.Message, e.ExitCode));
        Assert.Null(Files.Text(Evals("J-001.json")));
    }

    // --- feedback --------------------------------------------------------------------------------

    private FeedbackCommand Feedback => new(Paths, Files, Options.Create(_options), Clock);

    [Fact]
    public void Feedback_adds_the_humans_rating_to_the_snapshot_and_keeps_its_markdown()
    {
        Files.Put(Evals("J-001.json"), """{"job":"J-001","costUsd":1.0,"human":{"rating":"poor","at":"old"},"gateMs":null}""");
        var s = FakeSession.With();

        Feedback.Run(Args("feedback", "j-001", "-File", EvalFile("Rating: good\nnice")), s);

        Assert.Equal("""{"job":"J-001","costUsd":1.0,"human":{"rating":"good","at":"2026-11-10T12:00:00.000Z"},"gateMs":null}""", Json(Evals("J-001.json")).ToJsonString());
        Assert.Equal("Rating: good\nnice", Files.Text(Evals("j-001.feedback.md")));
        Assert.Equal(("feedback", "good"), ((string?)s.Events.Single()["kind"], (string?)s.Events.Single()["detail"]));
    }

    [Fact]
    public void Feedback_refuses_a_bad_id_an_unevaluated_job_a_missing_file_and_no_rating()
    {
        string Refusal(params string[] a) => Assert.Throws<FactoryException>(() => Feedback.Run(Args(a), FakeSession.With())).Message;
        Assert.Equal("feedback refused: '../x' is not a job id", Refusal("feedback", "../x"));
        Assert.Equal("feedback refused: J-001 has no evaluation", Refusal("feedback", "J-001", "-File", EvalFile()));
        Files.Put(Evals("J-001.json"), "{}");
        Assert.Equal("feedback file not found: none.md", Refusal("feedback", "J-001", "-File", "none.md"));
        Assert.Equal("feedback file not found: ", Refusal("feedback", "J-001"));
        Assert.Equal("no 'Rating: good|mixed|poor' first line in eval.md", Refusal("feedback", "J-001", "-File", EvalFile("Great")));
    }

    // --- lesson ----------------------------------------------------------------------------------

    private LessonCommand Lesson => new(Paths, Files, Options.Create(_options), Clock);

    [Fact]
    public void Lesson_on_a_clean_job_writes_nothing()
    {
        Files.Put(JobFile("J-001", "round-1/deploy.md"), "## Template gaps\n- None.\n");
        Files.Put(JobFile("J-001", "round-1/build.md"), "## Template gaps\n- src/x.cs: job-specific, only here\n");
        var s = FakeSession.With(Jobs.New("J-001", "ready"));

        Assert.Equal(["no lessons"], Lesson.Run(Args("lesson", "J-001"), s).Out);

        Assert.Null(Files.Text(Lessons("J-001.md")));
        Assert.Empty(s.Events);
    }

    [Fact]
    public void Lesson_compiles_the_flag_each_rounds_gaps_in_round_order_and_the_evaluation()
    {
        Files.Put(JobFile("J-001", "round-10/deploy.md"), "## Template gaps\n- tenth\n");
        Files.Put(JobFile("J-001", "round-2/deploy.md"), "## Template gaps\nnone\n");
        Files.Put(JobFile("J-001", "round-2/build.md"), "## Template gaps\n- infra/main.tf: job-specific, x\n- template.yml lacks a step\n- another\n");
        Files.Put(JobFile("J-001", "round-1/deploy.md"), "# Deploy\n\n## Template gaps\n- first\n\n## Next\nx\n");
        var job = Jobs.New("J-001", "blocked").With("flag", new JsonObject { ["reason"] = "deploy-failed", ["detail"] = "apply", ["why"] = "Terraform said no" });
        var s = FakeSession.With(job);

        Assert.Equal(["lessons/J-001.md"], Lesson.Run(Args("lesson", "J-001", "-File", EvalFile("Rating: poor\n\nit broke\n")), s).Out);

        var nl = Environment.NewLine;
        Assert.Equal(
            "# Lessons: J-001 - Thing\n\n- Target: o/r\n- Flag: deploy-failed: apply\n\n## Why it was flagged\nTerraform said no\n\n" +
            "## Template gaps (round-1)\n- first\n\n## Template gaps (round-2, build)\n- template.yml lacks a step\n- another\n\n" +
            "## Template gaps (round-10)\n- tenth\n\n## From the evaluation\nRating: poor\n\nit broke" + nl,
            Files.Text(Lessons("J-001.md")));
        Assert.Equal("""{"job":"J-001","title":"Thing","target":"o/r","flag":"deploy-failed","templateGaps":4,"status":"open","session":null,"at":"2026-11-10T12:00:00.000Z"}""",
            Json(Lessons("J-001.json")).ToJsonString());
        Assert.Equal(("lesson", "4"), ((string?)s.Events.Single()["kind"], (string?)s.Events.Single()["detail"]));
    }

    [Fact]
    public void Lesson_again_keeps_the_status_and_session_already_set_and_a_flag_with_no_why_has_no_why_section()
    {
        Files.Put(Lessons("J-001.json"), """{"status":"proposed","session":"S-004"}""");
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("flag", new JsonObject { ["reason"] = "max-rounds", ["detail"] = "3" }));

        Lesson.Run(Args("lesson", "J-001"), s);

        var info = Json(Lessons("J-001.json"));
        Assert.Equal(("proposed", "S-004", 0), ((string?)info["status"], (string?)info["session"], (int)info["templateGaps"]!));
        Assert.DoesNotContain("Why it was flagged", Files.Text(Lessons("J-001.md")));
    }

    [Fact]
    public void Lesson_refuses_a_missing_evaluation_file()
    {
        Assert.Equal("lesson file not found: gone.md",
            Assert.Throws<FactoryException>(() => Lesson.Run(Args("lesson", "J-001", "-File", "gone.md"), FakeSession.With(Jobs.New("J-001", "ready")))).Message);
    }

    // --- lesson-mark -----------------------------------------------------------------------------

    [Fact]
    public void Lesson_mark_sets_the_status_and_the_session_when_given()
    {
        Files.Put(Lessons("J-001.json"), """{"job":"J-001","status":"open","session":null}""");
        var sut = new LessonMarkCommand(Paths, Files);

        sut.Run(Args("lesson-mark", "J-001", "proposed", "-Detail", "S-002"), FakeSession.With());
        Assert.Equal("""{"job":"J-001","status":"proposed","session":"S-002"}""", Json(Lessons("J-001.json")).ToJsonString());

        sut.Run(Args("lesson-mark", "J-001", "APPLIED"), FakeSession.With());
        Assert.Equal("""{"job":"J-001","status":"APPLIED","session":"S-002"}""", Json(Lessons("J-001.json")).ToJsonString());
    }

    [Fact]
    public void Lesson_mark_refuses_a_bad_id_status_or_a_job_with_no_lessons()
    {
        var sut = new LessonMarkCommand(Paths, Files);
        string Refusal(params string[] a) => Assert.Throws<FactoryException>(() => sut.Run(Args(a), FakeSession.With())).Message;
        Assert.Equal("lesson-mark refused: 'x' is not a job id", Refusal("lesson-mark", "x", "open"));
        Assert.Equal("lesson-mark: status must be open, proposed or applied", Refusal("lesson-mark", "J-001", "done"));
        Assert.Equal("lesson-mark refused: J-001 has no lessons", Refusal("lesson-mark", "J-001", "open"));
    }

    // --- state admin -----------------------------------------------------------------------------

    private readonly IStateSettings _settings = Substitute.For<IStateSettings>();
    private readonly IStateSync _sync = Substitute.For<IStateSync>();
    private readonly IStateFirewall _firewall = Substitute.For<IStateFirewall>();

    [Fact]
    public void The_state_admin_commands_refuse_off_azure()
    {
        foreach (var (cmd, run) in new (string, Func<Factory.Cli.Abstractions.CommandResult>)[]
        {
            ("state-migrate", () => new StateMigrateCommand(_settings, _sync).Run(Args("state-migrate"), FakeSession.With())),
            ("state-pull", () => new StatePullCommand(_settings, _sync).Run(Args("state-pull"), FakeSession.With())),
            ("prune", () => new PruneCommand(_settings, _sync, Clock).Run(Args("prune"), FakeSession.With())),
            ("allow-ip", () => new AllowIpCommand(_settings, _firewall).Run(Args("allow-ip"), FakeSession.With())),
        })
            Assert.Equal($"{cmd} refused: FACTORY_STATE is not azure", Assert.Throws<FactoryException>(run).Message);
        Assert.Empty(_sync.ReceivedCalls());
    }

    [Fact]
    public void State_migrate_runs_on_an_unrefreshed_board_and_says_where_it_wrote()
    {
        _settings.IsAzure.Returns(true);
        _settings.ContainerUrl.Returns("https://st/state");
        _sync.Migrate().Returns(7);
        Factory.Cli.Abstractions.ICommand sut = new StateMigrateCommand(_settings, _sync);

        Assert.False(sut.RefreshesBoard);
        Assert.Equal(["migrated: 7 blobs to https://st/state"], sut.Run(Args("state-migrate"), FakeSession.With()).Out);
    }

    [Fact]
    public void State_pull_pulls_under_the_prefix_then_rebuilds_events()
    {
        _settings.IsAzure.Returns(true);
        _sync.Pull("jobs/").Returns(3);

        Assert.Equal(["pulled: 3"], new StatePullCommand(_settings, _sync).Run(Args("state-pull", "jobs/"), FakeSession.With()).Out);
        Received.InOrder(() => { _sync.Pull("jobs/"); _sync.SyncEvents(); });
    }

    [Fact]
    public void Prune_prints_the_size_against_the_cap_and_notes_what_it_deleted()
    {
        _settings.IsAzure.Returns(true);
        _sync.Prune(default!).ReturnsForAnyArgs(new PruneResult(900, 1000, 2, 300), new PruneResult(900, 1000, 0, 0));
        var s = FakeSession.With(Jobs.New("J-001", "done"));
        var sut = new PruneCommand(_settings, _sync, Clock);

        Assert.Equal(["900 of 1000 bytes"], sut.Run(Args("prune"), s).Out);
        Assert.Equal("pruned 2 blobs, 300 bytes freed", (string?)s.Events.Single()["detail"]);
        sut.Run(Args("prune"), s);
        Assert.Single(s.Events);
    }

    [Fact]
    public void Allow_ip_says_what_it_opened_and_what_it_closed()
    {
        _settings.IsAzure.Returns(true);
        _firewall.AllowIp().Returns(new AllowIpResult("1.2.3.4", null, "st"), new AllowIpResult("5.6.7.8", "1.2.3.4", "st"));
        var sut = new AllowIpCommand(_settings, _firewall);

        Assert.Equal(["allowed 1.2.3.4 on st"], sut.Run(Args("allow-ip"), FakeSession.With()).Out);
        Assert.Equal(["allowed 5.6.7.8 on st; removed 1.2.3.4"], sut.Run(Args("allow-ip"), FakeSession.With()).Out);
    }
}
