using System.Text.Json.Nodes;
using Factory.Server.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class ServerSessionTests : ServerTestBase
{
    private string SessionFile(string id, string name) => Path.Combine(Home, "sessions", id, name);

    private JsonObject Session(string id = "S-001") => JsonNode.Parse(Files.Text(SessionFile(id, "session.json"))!)!.AsObject();

    private JsonArray Chat(string id = "S-001") => JsonNode.Parse(Files.Text(SessionFile(id, "chat.json"))!)!["messages"]!.AsArray();

    private void PutSession(string json, string chat = """{"messages":[{"role":"user","content":[{"type":"text","text":"hi"}]},{"role":"assistant","content":[]}]}""")
    {
        Files.Put(SessionFile("S-001", "session.json"), json);
        Files.Put(SessionFile("S-001", "chat.json"), chat);
    }

    private void TurnStarted(int times = 1) =>
        Launcher.Received(times).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "idea-turn", "S-001" })));

    [Fact]
    public void A_new_session_on_an_existing_repo_clones_it_records_the_idea_and_starts_a_turn()
    {
        Factory.Run("check-target", "a/b").Returns(CallResult.Done("existing"));

        var r = Ideas.New("""{"target":"a/b","text":"  add tags  "}""");

        Assert.Equal("""{"ok":true,"message":"S-001"}""", r.Body);
        Factory.Received().Run("clone-target", "a/b", Path.Combine(Work, "sessions", "S-001"));
        var s = Session();
        Assert.Equal(("running", "add tags", false, "claude-opus-5-5"), ((string?)s["status"], (string?)s["text"], (bool)s["newTarget"]!, (string?)s["model"]));
        Assert.StartsWith("Target: a/b\nThis is an existing git repo on GitHub, cloned for you;", (string?)Chat()[0]!["content"]![0]!["text"]);
        Assert.Contains("\"kind\":\"idea\",\"text\":\"add tags\",\"target\":\"a/b\",\"newTarget\":false", Files.Text(SessionFile("S-001", "human.jsonl")));
        TurnStarted();
    }

    [Fact]
    public void Session_ids_follow_the_highest_one_there()
    {
        Factory.Run("check-target", "a/b").Returns(CallResult.Done("existing"));
        Files.Put(SessionFile("S-007", "human.jsonl"), "");

        Assert.Equal("""{"ok":true,"message":"S-008"}""", Ideas.New("""{"target":"a/b","text":"x"}""").Body);
    }

    [Fact]
    public void A_new_session_is_refused_without_the_modes_credential_without_text_and_when_check_target_refuses()
    {
        Settings.IdeaBlocker.Returns("no ANTHROPIC_API_KEY in .env");
        Assert.Equal("""{"ok":false,"message":"no ANTHROPIC_API_KEY in .env"}""", Ideas.New("""{"target":"a/b","text":"x"}""").Body);

        Settings.IdeaBlocker.Returns((string?)null);
        Assert.Equal("""{"ok":false,"message":"describe the idea first"}""", Ideas.New("""{"target":"a/b","text":"  "}""").Body);

        Factory.Run("check-target", "a/b").Returns(CallResult.Refuse("new-idea refused: a/b is archived"));
        Assert.Equal(409, Ideas.New("""{"target":"a/b","text":"x"}""").Status);
        Assert.Null(Files.Text(SessionFile("S-001", "session.json")));
        Launcher.DidNotReceive().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a[0] == "runner" || a[0] == "idea-turn"));
    }

    [Fact]
    public void Add_new_needs_an_owner_and_has_no_target_clone_or_check_target()
    {
        Assert.Equal($$"""{"ok":false,"message":"Add new needs GITHUB_OWNER in {{Options.EnvFile.Replace("\\", "\\\\")}}"}""", Ideas.New("""{"new":true,"text":"x"}""").Body);

        Settings.Env("GITHUB_OWNER").Returns("kamabery");
        Assert.Equal(200, Ideas.New("""{"new":true,"text":"x"}""").Status);

        Factory.DidNotReceive().Run("check-target", Arg.Any<string>());
        Factory.DidNotReceive().Run("clone-target", Arg.Any<string>(), Arg.Any<string>());
        Assert.Contains("The owner is kamabery.", (string?)Chat()[0]!["content"]![0]!["text"]);
    }

    [Fact]
    public void A_base_is_refused_for_a_new_repo_and_otherwise_cloned_and_named()
    {
        Factory.Run("check-target", "a/new").Returns(CallResult.Done("new"));
        Assert.Equal("""{"ok":false,"message":"base refused: a new repo has no branches to build on"}""", Ideas.New("""{"target":"a/new","text":"x","base":"J-001"}""").Body);

        Factory.Run("check-target", "a/b").Returns(CallResult.Done("existing"));
        SetBoard(("J-001", "ready", true));
        Ideas.New("""{"target":"a/b","text":"x","base":"J-001"}""");

        Factory.Received().Run("clone-target", "a/b", Path.Combine(Work, "sessions", "S-001"), "-Base", "J-001");
        Assert.Contains("at branch factory/J-001, not main: the unmerged work of J-001 (t J-001)", (string?)Chat()[0]!["content"]![0]!["text"]);
    }

    [Fact]
    public void An_unknown_closed_or_busy_session_is_refused()
    {
        Assert.Equal(404, Ideas.Handle("POST", "S-001", "reply", null, "{}").Status);

        PutSession("""{"id":"S-001","status":"closed"}""");
        Assert.Equal("""{"ok":false,"message":"S-001 is closed"}""", Ideas.Handle("POST", "S-001", "reply", null, "{}").Body);

        PutSession("""{"id":"S-001","status":"idle"}""");
        Files.Put(SessionFile("S-001", "turn.lock"), "321");
        Procs.IsAlive(321).Returns(true);
        Assert.Equal("""{"ok":false,"message":"S-001 is busy: wait for the current turn"}""", Ideas.Handle("POST", "S-001", "reply", null, """{"text":"x"}""").Body);
    }

    [Fact]
    public void A_session_just_set_running_counts_as_busy_for_15_s()
    {
        PutSession("""{"id":"S-001","status":"running"}""");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(Files.Stat(SessionFile("S-001", "session.json"))!.LastWriteTicks, TimeSpan.Zero).AddSeconds(10));
        var ideas = new Factory.Server.IdeaSessions(Factory, Launcher, Paths, Files, Settings, Board, new Factory.Server.SessionFiles(Paths, Files, Procs, Opts, clock), Jobs, Opts, clock);

        Assert.Equal(409, ideas.Handle("POST", "S-001", "reply", null, """{"text":"x"}""").Status);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(200, ideas.Handle("POST", "S-001", "reply", null, """{"text":"x"}""").Status);
    }

    [Fact]
    public void A_reply_answers_the_pending_tool_with_any_server_results_and_records_what_it_answered()
    {
        PutSession("""{"id":"S-001","status":"idle","pending":{"id":"tu_1","name":"ask","input":{"q":1}},"pendingResults":[{"type":"tool_result","tool_use_id":"tu_0","content":"files"}]}""");

        var r = Ideas.Handle("POST", "S-001", "reply", null, """{"text":"Just me"}""");

        Assert.Equal("""{"ok":true,"message":"turn started"}""", r.Body);
        var last = Chat()[^1]!;
        Assert.Equal("""{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_0","content":"files"},{"type":"tool_result","tool_use_id":"tu_1","content":"Just me"}]}""", last.ToJsonString());
        Assert.Equal((null, 0), ((string?)Session()["pending"], Session()["pendingResults"]!.AsArray().Count));
        Assert.Contains("\"kind\":\"answer\",\"text\":\"Just me\",\"asked\":{\"name\":\"ask\",\"input\":{\"q\":1}}", Files.Text(SessionFile("S-001", "human.jsonl")));
        TurnStarted();
    }

    [Fact]
    public void A_reply_with_text_joins_the_last_user_message_and_one_with_nothing_to_answer_is_refused()
    {
        PutSession("""{"id":"S-001","status":"idle"}""", """{"messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}]}""");

        Ideas.Handle("POST", "S-001", "reply", null, """{"text":"more"}""");
        Assert.Equal(2, Chat()[0]!["content"]!.AsArray().Count);

        PutSession("""{"id":"S-001","status":"idle"}""");
        Assert.Equal("""{"ok":false,"message":"nothing to answer"}""", Ideas.Handle("POST", "S-001", "reply", null, "").Body);
        TurnStarted(1);
    }

    [Fact]
    public void Confirming_a_scaffold_refuses_a_repo_that_exists_and_otherwise_sets_the_target_and_keeps_repo_out()
    {
        Settings.Env("GITHUB_OWNER").Returns("me");
        PutSession("""{"id":"S-001","status":"idle","target":null,"newTarget":true,"pending":{"id":"tu_9","name":"propose_scaffold","input":{}}}""");
        Factory.Run("check-target", "me/taken").Returns(CallResult.Done("existing"));

        Assert.Equal("""{"ok":false,"message":"me/taken already exists: pick it from the list instead"}""",
            Ideas.Handle("POST", "S-001", "reply", null, """{"text":"ok","scaffold":{"repo":"taken","ui":"react"}}""").Body);
        Assert.Null((string?)Session()["target"]);

        Factory.Run("check-target", "me/fresh").Returns(CallResult.Done("new"));
        Ideas.Handle("POST", "S-001", "reply", null, """{"text":"ok","scaffold":{"repo":"fresh","ui":"react"}}""");

        Assert.Equal(("me/fresh", """{"ui":"react"}"""), ((string?)Session()["target"], Session()["scaffold"]!.ToJsonString()));
        Assert.Contains("ok The target is me/fresh.", Chat()[^1]!.ToJsonString());
    }

    [Fact]
    public void Confirming_the_brief_runs_new_idea_writes_brief_md_and_starts_the_spec_turn()
    {
        PutSession("""{"id":"S-001","status":"idle","target":"a/b","text":"x","pending":{"id":"tu_2","name":"submit_brief","input":{"markdown":"# Brief: Tag todos\n\nbody"}}}""");
        Factory.Run("new-idea", "Tag todos", "-Target", "a/b").Returns(CallResult.Done("I-003"));

        var r = Ideas.Handle("POST", "S-001", "brief", null, "");

        Assert.Equal("""{"ok":true,"message":"I-003"}""", r.Body);
        Assert.Equal("# Brief: Tag todos\n\nbody" + Environment.NewLine, Files.Text(Path.Combine(Home, "ideas", "I-003", "brief.md")));
        Assert.Equal("I-003", (string?)Session()["idea"]);
        Assert.Contains("Now be the spec-writer", Chat()[^1]!.ToJsonString());
        TurnStarted();
    }

    [Fact]
    public void Confirming_the_brief_of_a_session_on_a_base_makes_the_idea_on_that_base()
    {
        PutSession("""{"id":"S-001","status":"idle","target":"a/b","base":"J-001","text":"x","pending":{"id":"tu_2","name":"submit_brief","input":{"markdown":"# Brief: Tag todos\n\nbody"}}}""");
        Factory.Run("new-idea", "Tag todos", "-Target", "a/b", "-Base", "J-001").Returns(CallResult.Done("I-003"));

        var r = Ideas.Handle("POST", "S-001", "brief", null, "");

        Assert.Equal("""{"ok":true,"message":"I-003"}""", r.Body);
        Factory.DidNotReceive().Run("new-idea", "Tag todos", "-Target", "a/b");
    }

    [Fact]
    public void A_brief_for_a_new_target_needs_the_scaffold_and_its_bootstrap_job_starts_no_turn()
    {
        PutSession("""{"id":"S-001","status":"idle","target":"me/fresh","newTarget":true,"pending":{"id":"tu_3","name":"submit_brief","input":{"markdown":"no title"}}}""");
        Assert.Equal("""{"ok":false,"message":"confirm the scaffold options first"}""", Ideas.Handle("POST", "S-001", "brief", null, "").Body);

        PutSession("""{"id":"S-001","status":"idle","text":"the idea","target":"me/fresh","newTarget":true,"scaffold":{"ui":"react"},"pending":{"id":"tu_3","name":"submit_brief","input":{"markdown":"no title"}}}""");
        Factory.Run("new-idea", "the idea", "-Target", "me/fresh", "-Scaffold", """{"ui":"react"}""").Returns(CallResult.Done("I-004 J-010"));

        Assert.Equal(200, Ideas.Handle("POST", "S-001", "brief", null, "").Status);
        Assert.Equal(("I-004", "J-010", "idle"), ((string?)Session()["idea"], (string?)Session()["bootstrap"], (string?)Session()["status"]));
        Launcher.DidNotReceive().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a[0] == "runner" || a[0] == "idea-turn"));
    }

    [Fact]
    public void There_is_no_brief_to_confirm_without_a_pending_submit_brief()
    {
        PutSession("""{"id":"S-001","status":"idle","pending":{"id":"tu","name":"ask"}}""");
        Assert.Equal("""{"ok":false,"message":"there is no brief to confirm"}""", Ideas.Handle("POST", "S-001", "brief", null, "").Body);
    }

    [Fact]
    public void Write_specs_waits_for_the_bootstrap_merge_then_clones_the_new_target()
    {
        PutSession("""{"id":"S-001","status":"idle","target":"me/fresh","idea":"I-004","bootstrap":"J-010"}""");
        Files.Put(Path.Combine(Home, "board.json"), """{"jobs":[],"ideas":[{"id":"I-004","status":"waiting"}]}""");
        Assert.Equal("""{"ok":false,"message":"I-004 is waiting: merge the bootstrap job J-010 first"}""", Ideas.Handle("POST", "S-001", "write-specs", null, "").Body);

        Files.Put(Path.Combine(Home, "board.json"), """{"jobs":[],"ideas":[{"id":"I-004","status":"drafting"}]}""");
        Assert.Equal(200, Ideas.Handle("POST", "S-001", "write-specs", null, "").Status);
        Factory.Received().Run("clone-target", "me/fresh", Path.Combine(Work, "sessions", "S-001"));
        TurnStarted();

        PutSession("""{"id":"S-001","status":"idle"}""");
        Assert.Equal("""{"ok":false,"message":"the brief is not confirmed yet"}""", Ideas.Handle("POST", "S-001", "write-specs", null, "").Body);
    }

    [Fact]
    public void Approving_a_spec_adds_and_approves_it_and_starts_the_runner_and_a_failed_approve_leaves_it_in_backlog()
    {
        PutSession("""{"id":"S-001","status":"idle","idea":"I-003","specs":[{"k":1,"title":"a"},{"k":2,"title":"b"},{"k":3,"title":"c","decision":"rejected"}]}""");
        var spec1 = Path.Combine(Home, "ideas", "I-003", "spec-1.md");
        var spec2 = Path.Combine(Home, "ideas", "I-003", "spec-2.md");
        Factory.Run("add-spec", "I-003", spec1).Returns(CallResult.Done("J-011"));
        Factory.Run("add-spec", "I-003", spec2).Returns(CallResult.Done("J-012"));
        Factory.Run("approve", "J-012").Returns(CallResult.Refuse("approve refused: no Gate"));

        Assert.Equal(200, Ideas.Handle("POST", "S-001", "specs", "1", """{"decision":"approved"}""").Status);
        var failed = Ideas.Handle("POST", "S-001", "specs", "2", """{"decision":"approved"}""");

        Assert.Equal("""{"ok":false,"message":"J-012 is in Backlog: approve refused: no Gate"}""", failed.Body);
        var specs = Session()["specs"]!.AsArray();
        Assert.Equal(("approved", "J-011", "backlog", "J-012"), ((string?)specs[0]!["decision"], (string?)specs[0]!["job"], (string?)specs[1]!["decision"], (string?)specs[1]!["job"]));
        Launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "runner" })));
        Assert.Equal("""{"ok":false,"message":"spec 3 is already decided (rejected)"}""", Ideas.Handle("POST", "S-001", "specs", "3", """{"decision":"approved"}""").Body);
        Assert.Equal("""{"ok":false,"message":"there is no spec 9"}""", Ideas.Handle("POST", "S-001", "specs", "9", """{"decision":"approved"}""").Body);
    }

    [Fact]
    public void Not_yet_adds_the_spec_to_backlog_rejected_calls_nothing_and_anything_else_is_refused()
    {
        PutSession("""{"id":"S-001","status":"idle","idea":"I-003","specs":[{"k":1},{"k":2},{"k":3}]}""");
        Factory.Run("add-spec", Arg.Any<string>(), Arg.Any<string>()).Returns(CallResult.Done("J-013"));

        Ideas.Handle("POST", "S-001", "specs", "1", """{"decision":"backlog"}""");
        Ideas.Handle("POST", "S-001", "specs", "2", """{"decision":"rejected"}""");
        var bad = Ideas.Handle("POST", "S-001", "specs", "3", """{"decision":"maybe"}""");

        Factory.Received(1).Run("add-spec", Arg.Any<string>(), Arg.Any<string>());
        Factory.DidNotReceive().Run("approve", Arg.Any<string>());
        Assert.Equal("""{"ok":false,"message":"decision 'maybe' must be approved, backlog or rejected"}""", bad.Body);
        Assert.Equal(("backlog", "rejected"), ((string?)Session()["specs"]![0]!["decision"], (string?)Session()["specs"]![1]!["decision"]));
    }

    [Fact]
    public void A_direct_edit_needs_a_spec_title_and_rewrites_the_spec()
    {
        PutSession("""{"id":"S-001","status":"idle","idea":"I-003","specs":[{"k":1,"title":"old"}]}""");

        Assert.Equal("""{"ok":false,"message":"the spec needs a '# Spec: <title>' line"}""", Ideas.Handle("PUT", "S-001", "specs", "1", "no title").Body);
        Assert.Equal("""{"ok":true,"message":"spec saved"}""", Ideas.Handle("PUT", "S-001", "specs", "1", "# Spec: New one\n\nbody").Body);

        Assert.Equal("# Spec: New one\n\nbody", Files.Text(Path.Combine(Home, "ideas", "I-003", "spec-1.md")));
        Assert.Equal("New one", (string?)Session()["specs"]![0]!["title"]);
    }

    [Fact]
    public void Close_marks_the_idea_done_failed_or_abandoned_and_closes_the_session()
    {
        PutSession("""{"id":"S-001","status":"idle","idea":"I-003"}""");
        Ideas.Handle("POST", "S-001", "close", null, "{}");
        Factory.Received().Run("idea-done", "I-003");
        Assert.Equal("closed", (string?)Session()["status"]);

        PutSession("""{"id":"S-001","status":"idle","idea":"I-003"}""");
        Ideas.Handle("POST", "S-001", "close", null, """{"failed":true}""");
        Factory.Received().Run("idea-done", "I-003", "-Failed", "-Reason", "every spec rejected in session");

        PutSession("""{"id":"S-001","status":"idle","idea":"I-003"}""");
        Factory.Run("idea-abandon", "I-003").Returns(CallResult.Refuse("I-003 is done"));
        Assert.Equal(409, Ideas.Handle("POST", "S-001", "close", null, """{"abandon":true}""").Status);
        Assert.Equal("idle", (string?)Session()["status"]);
    }

    [Fact]
    public void Closing_a_session_with_no_idea_calls_nothing()
    {
        PutSession("""{"id":"S-001","status":"idle"}""");

        Assert.Equal("""{"ok":true,"message":"closed"}""", Ideas.Handle("POST", "S-001", "close", null, """{"abandon":true}""").Body);
        Factory.DidNotReceive().Run(Arg.Any<string[]>());
    }
}
