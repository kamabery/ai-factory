using System.Text.Json.Nodes;
using Factory.Server.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>
/// Spec 21: a new-repo session runs brief → scaffold → specs → build with one approval (the brief) and one merge
/// (contract, "Idea sessions (dashboard)", "Advancing and notices").
/// </summary>
public class ServerSessionFlowTests : ServerTestBase
{
    private string SessionFile(string id, string name) => Path.Combine(Home, "sessions", id, name);

    private JsonObject Session(string id = "S-001") => JsonNode.Parse(Files.Text(SessionFile(id, "session.json"))!)!.AsObject();

    private void PutSession(string json, string id = "S-001")
    {
        Files.Put(SessionFile(id, "session.json"), json);
        Files.Put(SessionFile(id, "chat.json"), """{"messages":[{"role":"user","content":[{"type":"text","text":"hi"}]},{"role":"assistant","content":[]}]}""");
    }

    private void PutBoard(string bootStage, string ideaStatus) =>
        Files.Put(Path.Combine(Home, "board.json"),
            $$"""{"jobs":[{"id":"J-010","stage":"{{bootStage}}","title":"Bootstrap","kind":"bootstrap"}],"ideas":[{"id":"I-004","status":"{{ideaStatus}}"}]}""");

    private const string Waiting = """{"id":"S-001","status":"idle","target":"me/fresh","newTarget":true,"idea":"I-004","bootstrap":"J-010"}""";

    private int Turns() => Launcher.ReceivedCalls().Count(c => c.GetArguments()[0] is IReadOnlyList<string> a && a.SequenceEqual(new[] { "idea-turn", "S-001" }));

    private int Runners() => Launcher.ReceivedCalls().Count(c => c.GetArguments()[0] is IReadOnlyList<string> a && a.SequenceEqual(new[] { "runner" }));

    [Fact]
    public void Confirming_a_new_targets_brief_approves_the_bootstrap_and_starts_the_runner()
    {
        PutSession("""{"id":"S-001","status":"idle","text":"the idea","target":"me/fresh","newTarget":true,"scaffold":{"ui":"react"},"pending":{"id":"tu_3","name":"submit_brief","input":{"markdown":"# Brief: Fresh"}}}""");
        Factory.Run("new-idea", "Fresh", "-Target", "me/fresh", "-Scaffold", """{"ui":"react"}""").Returns(CallResult.Done("I-004 J-010"));

        var r = Ideas.Handle("POST", "S-001", "brief", null, "");

        Assert.Equal(200, r.Status);
        Factory.Received(1).Run("approve", "J-010");
        Assert.Equal(1, Runners());
        Assert.Equal(0, Turns());
        Assert.Equal(("I-004", "J-010", "idle"), ((string?)Session()["idea"], (string?)Session()["bootstrap"], (string?)Session()["status"]));
    }

    [Fact]
    public void A_refused_bootstrap_approval_leaves_it_in_backlog_and_answers_the_refusal()
    {
        PutSession("""{"id":"S-001","status":"idle","text":"the idea","target":"me/fresh","newTarget":true,"scaffold":{"ui":"react"},"pending":{"id":"tu_3","name":"submit_brief","input":{"markdown":"# Brief: Fresh"}}}""");
        Factory.Run("new-idea", "Fresh", "-Target", "me/fresh", "-Scaffold", """{"ui":"react"}""").Returns(CallResult.Done("I-004 J-010"));
        Factory.Run("approve", "J-010").Returns(CallResult.Refuse("approve refused: J-010 needs a canary"));

        var r = Ideas.Handle("POST", "S-001", "brief", null, "");

        Assert.Equal("""{"ok":false,"message":"J-010 is in Backlog: approve refused: J-010 needs a canary"}""", r.Body);
        Assert.Equal(0, Runners());
        // The brief is still confirmed: the idea and its bootstrap are the session's.
        Assert.Equal(("I-004", "J-010", null), ((string?)Session()["idea"], (string?)Session()["bootstrap"], (string?)Session()["pending"]));
    }

    [Fact]
    public void A_sweep_starts_the_spec_turn_once_the_bootstrap_has_merged_and_only_once()
    {
        PutSession(Waiting);
        PutBoard("ready", "waiting");
        Ideas.Sweep();
        Assert.Equal(0, Turns());

        PutBoard("done", "drafting");
        Ideas.Sweep();

        Assert.Equal(1, Turns());
        Factory.Received(1).Run("clone-target", "me/fresh", Path.Combine(Work, "sessions", "S-001"));
        Assert.Equal("2026-11-10T12:00:00.0000000Z", (string?)Session()["specsStarted"]);
        Assert.Contains("has merged", Files.Text(SessionFile("S-001", "chat.json")));

        // The turn has ended in error, with no specs: the sweep does not start it again (Retry does).
        var s = Session();
        s["status"] = "error";
        Files.Put(SessionFile("S-001", "session.json"), s.ToJsonString());
        Ideas.Sweep();
        Assert.Equal(1, Turns());
    }

    [Fact]
    public void A_sweep_leaves_a_session_alone_while_a_turn_runs_or_once_it_has_specs()
    {
        PutBoard("done", "drafting");
        PutSession(Waiting);
        Files.Put(SessionFile("S-001", "turn.lock"), "321");
        Procs.IsAlive(321).Returns(true);
        Ideas.Sweep();

        PutSession("""{"id":"S-001","status":"idle","target":"me/fresh","newTarget":true,"idea":"I-004","bootstrap":"J-010","specs":[{"k":1,"title":"a"}]}""");
        Procs.IsAlive(321).Returns(false);
        Ideas.Sweep();

        PutSession("""{"id":"S-001","status":"closed","target":"me/fresh","newTarget":true,"idea":"I-004","bootstrap":"J-010"}""");
        Ideas.Sweep();

        Assert.Equal(0, Turns());
    }

    [Fact]
    public void Merging_the_bootstrap_from_the_board_or_the_session_starts_the_specs_at_once()
    {
        PutSession(Waiting);
        PutBoard("ready", "waiting");
        Factory.Run("merge", "J-010").Returns(_ => { PutBoard("done", "drafting"); return CallResult.Done("merged J-010"); });

        var r = Ideas.Move("J-010", """{"to":"done"}""");

        Assert.Equal("""{"ok":true,"message":"merged J-010"}""", r.Body);
        Assert.Equal(1, Turns());
    }

    [Fact]
    public void A_refused_move_still_answers_the_refusal()
    {
        PutSession(Waiting);
        PutBoard("ready", "waiting");
        Factory.Run("merge", "J-010").Returns(CallResult.Refuse("merge refused: the gate is red"));

        Assert.Equal("""{"ok":false,"message":"merge refused: the gate is red"}""", Ideas.Move("J-010", """{"to":"done"}""").Body);
        Assert.Equal(0, Turns());
    }

    [Fact]
    public void The_two_notices_are_recorded_once_each()
    {
        PutSession(Waiting);
        PutBoard("build", "waiting");
        Ideas.Sweep();
        Assert.Null(Session()["notices"]?["scaffold-ready"]);

        PutBoard("ready", "waiting");
        Ideas.Sweep();
        Assert.Equal("2026-11-10T12:00:00.0000000Z", (string?)Session()["notices"]!["scaffold-ready"]);

        Clock.Advance(TimeSpan.FromMinutes(5));
        Ideas.Sweep();
        Assert.Equal("2026-11-10T12:00:00.0000000Z", (string?)Session()["notices"]!["scaffold-ready"]);
        Assert.Null(Session()["notices"]!["specs-ready"]);

        // The spec turn has written specs and ended.
        var s = Session();
        s["specs"] = JsonNode.Parse("""[{"k":1,"title":"a"}]""");
        s["specsStarted"] = "x";
        Files.Put(SessionFile("S-001", "session.json"), s.ToJsonString());
        PutBoard("done", "drafting");
        Ideas.Sweep();
        Clock.Advance(TimeSpan.FromMinutes(5));
        Ideas.Sweep();

        Assert.Equal("2026-11-10T12:05:00.0000000Z", (string?)Session()["notices"]!["specs-ready"]);
        Assert.Equal("2026-11-10T12:00:00.0000000Z", (string?)Session()["notices"]!["scaffold-ready"]);
    }

    [Fact]
    public void No_specs_ready_notice_while_the_spec_turn_is_still_running()
    {
        PutBoard("done", "drafting");
        PutSession("""{"id":"S-001","status":"running","idea":"I-004","bootstrap":"J-010","specs":[{"k":1}],"specsStarted":"x"}""");
        Files.Put(SessionFile("S-001", "turn.lock"), "321");
        Procs.IsAlive(321).Returns(true);

        Ideas.Sweep();

        Assert.Null(Session()["notices"]);
    }

    [Fact]
    public void Write_specs_records_that_the_specs_were_started()
    {
        PutSession(Waiting);
        PutBoard("done", "drafting");

        Assert.Equal(200, Ideas.Handle("POST", "S-001", "write-specs", null, "").Status);

        Assert.NotNull((string?)Session()["specsStarted"]);
        Ideas.Sweep();
        Assert.Equal(1, Turns());
    }

    [Fact]
    public void The_session_summaries_carry_the_phase_and_the_notices_and_one_session_reads_even_when_closed()
    {
        PutBoard("ready", "waiting");
        PutSession("""{"id":"S-001","status":"idle","text":"x","target":"me/fresh","newTarget":true,"idea":"I-004","bootstrap":"J-010","notices":{"scaffold-ready":"t"}}""");
        PutSession("""{"id":"S-002","status":"closed","text":"y","idea":"I-005"}""", "S-002");

        var list = JsonNode.Parse(Reads.Sessions().Body)!.AsArray();

        var one = Assert.Single(list)!;
        Assert.Equal("""{"name":"scaffold","job":"J-010","stage":"ready","pr":null}""", one["phase"]!.ToJsonString());
        Assert.Equal("""{"scaffold-ready":"t"}""", one["notices"]!.ToJsonString());
        Assert.Equal("specs", (string?)JsonNode.Parse(Reads.Session("S-002").Body)!["phase"]!["name"]);
        Assert.Equal("closed", (string?)JsonNode.Parse(Reads.Session("S-002").Body)!["status"]);
        Assert.Equal(404, Reads.Session("S-009").Status);
    }

    [Fact]
    public void The_watchdog_sweeps_the_sessions_on_its_tick()
    {
        PutSession(Waiting);
        PutBoard("done", "drafting");

        Dog.Tick();

        Assert.Equal(1, Turns());
    }
}
