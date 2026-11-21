using Factory.Core.Abstractions;
using Factory.Server;
using Factory.Server.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class ServerJobsTests : ServerTestBase
{
    [Fact]
    public void A_factory_call_answers_ok_on_exit_0_with_stdout_and_stderr_trimmed_and_no_factory_prefix()
    {
        var processes = Substitute.For<IProcessRunner>();
        Paths.CliDll.Returns("factory.dll");
        processes.Run("dotnet", Arg.Any<IReadOnlyList<string>>(), "").Returns(new ProcessResult(1, "", "factory: approve refused: J-001 is in build\r\n"));
        var calls = new FactoryCalls(Paths, processes);

        Assert.Equal(new CallResult(false, "approve refused: J-001 is in build"), calls.Run("approve", "J-001"));
        processes.Received().Run("dotnet", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "factory.dll", "approve", "J-001" })), "");

        processes.Run("dotnet", Arg.Any<IReadOnlyList<string>>(), "").Returns(new ProcessResult(0, "I-001 J-001\n", "warn\n"));
        Assert.Equal(new CallResult(true, "I-001 J-001\nwarn"), calls.Run("new-idea"));
    }

    [Fact]
    public void A_result_is_200_when_the_CLI_succeeded_and_409_when_it_refused()
    {
        Assert.Equal(new Reply(200, """{"ok":true,"message":"done"}"""), Reply.Of(CallResult.Done("done")));
        Assert.Equal(new Reply(409, """{"ok":false,"message":"no <way>"}"""), Reply.Of(CallResult.Refuse("no <way>")));
    }

    [Fact]
    public void Approve_from_backlog_calls_approve_and_starts_the_runner()
    {
        SetBoard(("J-001", "backlog", false));

        var r = Jobs.Move("J-001", """{"to":"approved"}""");

        Assert.Equal(200, r.Status);
        Factory.Received().Run("approve", "J-001");
        Launcher.Received().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "runner" })));
    }

    [Fact]
    public void A_refused_move_starts_no_runner()
    {
        SetBoard(("J-001", "build", false));
        Factory.Run("approve", "J-001").Returns(CallResult.Refuse("approve refused"));

        var r = Jobs.Move("J-001", """{"to":"approved"}""");

        Assert.Equal((409, """{"ok":false,"message":"approve refused"}"""), (r.Status, r.Body));
        Launcher.DidNotReceive().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a[0] == "runner" || a[0] == "idea-turn"));
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("blocked")]
    public void Back_to_approved_from_ready_or_blocked_is_a_rework_and_needs_a_note(string stage)
    {
        SetBoard(("J-002", stage, false));

        var refused = Jobs.Move("J-002", """{"to":"approved"}""");
        var reworked = Jobs.Move("J-002", """{"to":"approved","note":"fix the test"}""");

        Assert.Equal((409, """{"ok":false,"message":"rework refused: a rework needs a note"}"""), (refused.Status, refused.Body));
        Assert.Equal(200, reworked.Status);
        Factory.Received(1).Run("rework", "J-002", "fix the test");
        Factory.DidNotReceive().Run("approve", Arg.Any<string>());
    }

    [Fact]
    public void Done_merges_discarded_discards_and_anything_else_is_not_a_human_move()
    {
        SetBoard(("J-003", "ready", false));

        Jobs.Move("J-003", """{"to":"done"}""");
        Jobs.Move("J-003", """{"to":"discarded"}""");
        var other = Jobs.Move("J-003", """{"to":"review"}""");

        Factory.Received().Run("merge", "J-003");
        Factory.Received().Run("discard", "J-003");
        Assert.Equal("""{"ok":false,"message":"move to 'review' refused: not a human move"}""", other.Body);
        Launcher.DidNotReceive().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a[0] == "runner" || a[0] == "idea-turn"));
    }

    [Fact]
    public void Moving_a_job_that_is_not_on_the_board_is_a_404()
    {
        SetBoard();
        Assert.Equal((404, """{"ok":false,"message":"no such job"}"""), (Jobs.Move("J-404", """{"to":"done"}""").Status, Jobs.Move("J-404", """{"to":"done"}""").Body));
    }

    [Fact]
    public void Resume_and_redeploy_start_the_runner_only_when_factory_ps1_moved_the_job()
    {
        Factory.Run("redeploy", "J-005").Returns(CallResult.Refuse("redeploy refused"));

        Assert.Equal(200, Jobs.Resume("J-004").Status);
        Assert.Equal(409, Jobs.Redeploy("J-005").Status);

        Factory.Received().Run("resume", "J-004");
        Launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "runner" })));
    }

    [Fact]
    public void Tear_down_and_run_canary_answer_at_once_and_run_detached()
    {
        Assert.Equal("""{"ok":true,"message":"tearing down the preview"}""", Jobs.Destroy("J-006").Body);
        Assert.Equal("""{"ok":true,"message":"canary started"}""", Jobs.Canary().Body);

        Launcher.Received().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "destroy", "J-006" })));
        Launcher.Received().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "canary", "-Force" })));
    }

    [Fact]
    public void In_the_cloud_run_canary_queues_a_retry_and_starts_a_runner_execution_never_a_canary_here()
    {
        Runner = Substitute.For<IRunnerStarter>();
        Runner.StartsExecutions.Returns(true);
        Runner.Start().Returns("started runner execution r-3");
        Factory.Run("canary", "-Retry").Returns(CallResult.Done("canary: retry queued"));

        Assert.Equal("""{"ok":true,"message":"canary: retry queued; started runner execution r-3"}""", Jobs.Canary().Body);

        Launcher.DidNotReceiveWithAnyArgs().StartDetached(default!);
    }

    [Fact]
    public void Run_canary_is_a_409_while_a_live_canary_holds_canary_lock_and_not_once_it_is_dead()
    {
        Files.Put(Path.Combine(Home, "canary.lock"), "777\n");
        Procs.IsAlive(777).Returns(true);

        var live = Jobs.Canary();
        Procs.IsAlive(777).Returns(false);
        var dead = Jobs.Canary();

        Assert.Equal((409, """{"ok":false,"message":"a canary is already running"}"""), (live.Status, live.Body));
        Assert.Equal(200, dead.Status);
        Launcher.Received(1).StartDetached(Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public void Qa_login_is_factory_ps1s_answer()
    {
        Factory.Run("qa-login", "J-007").Returns(CallResult.Done("user: qa\npassword: x"));
        Assert.Equal("""{"ok":true,"message":"user: qa\npassword: x"}""", Jobs.QaLogin("J-007").Body);
    }

    [Fact]
    public void Files_lists_the_jobs_artifacts_relative_and_sorted_after_pulling_the_job()
    {
        Files.Put(JobFile("J-008", "spec.md"), "s");
        Files.Put(JobFile("J-008", "round-1/build.md"), "b");
        Files.Put(JobFile("J-008", "Decision.md"), "d");

        Assert.Equal("""["Decision.md","round-1/build.md","spec.md"]""", Jobs.Files("J-008").Body);
        Cache.Received().Job("J-008");
    }

    [Fact]
    public void A_transcript_is_only_the_file_an_agent_start_event_names_or_its_copy_in_the_job()
    {
        var local = Path.Combine(Home, "t.jsonl");
        Files.Put(local, "x");
        Files.Put(Path.Combine(Home, "events.jsonl"),
            $$"""
            {"kind":"agent-start","agentId":"a1","job":"J-009","transcript":"{{local.Replace("\\", "\\\\")}}"}
            {"kind":"agent-start","agentId":"a2","job":"J-009","transcript":"/elsewhere/t.jsonl"}
            {"kind":"agent-stop","agentId":"a3"}
            """);

        Assert.Equal(local, Jobs.Transcript("a1").FilePath);
        Assert.Equal(JobFile("J-009", "transcripts/agent-a2.jsonl"), Jobs.Transcript("a2").FilePath);
        Assert.Equal(404, Jobs.Transcript("a3").Status);
        Cache.Received().Job("J-009");
    }
}
