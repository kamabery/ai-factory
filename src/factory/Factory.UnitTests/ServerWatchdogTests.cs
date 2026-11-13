using NSubstitute;

namespace Factory.UnitTests;

public class ServerWatchdogTests : ServerTestBase
{
    private string RunnerLock => Path.Combine(Home, "runner.lock");

    [Fact]
    public void Starts_the_runner_and_logs_it_when_approved_is_not_empty_and_no_runner_holds_the_lock()
    {
        SetBoard(("J-001", "approved", false));

        Dog.Tick();

        Launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "runner" })));
        Assert.Equal($"2026-11-10T12:00:00.0000000Z watchdog: started runner{Environment.NewLine}", Files.Text(Path.Combine(Home, "runner.log")));
    }

    [Fact]
    public void Does_nothing_while_approved_is_empty()
    {
        SetBoard(("J-001", "ready", false), ("J-002", "backlog", false));

        Dog.Tick();

        Launcher.DidNotReceive().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a[0] == "runner" || a[0] == "idea-turn"));
    }

    [Fact]
    public void Leaves_a_live_runner_alone_and_takes_over_from_a_dead_one()
    {
        SetBoard(("J-001", "approved", false));
        Files.Put(RunnerLock, "555");
        Procs.IsAlive(555).Returns(true);
        var dog = Dog;

        dog.Tick();
        Launcher.DidNotReceive().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a[0] == "runner" || a[0] == "idea-turn"));

        Procs.IsAlive(555).Returns(false);
        Clock.Advance(TimeSpan.FromSeconds(10));
        dog.Tick();
        Launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "runner" })));
    }

    [Fact]
    public void A_lock_younger_than_5_s_may_not_hold_its_pid_yet_so_it_counts_as_held()
    {
        SetBoard(("J-001", "approved", false));
        Files.Put(RunnerLock, "");
        var written = new DateTimeOffset(Files.Stat(RunnerLock)!.LastWriteTicks, TimeSpan.Zero);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(written.AddSeconds(2));

        new Factory.Server.Watchdog(Board, Paths, Files, Launcher, Procs, Opts, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<Factory.Server.Watchdog>.Instance).Tick();

        Launcher.DidNotReceive().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a[0] == "runner" || a[0] == "idea-turn"));
    }

    [Fact]
    public void Checks_again_only_after_its_interval()
    {
        SetBoard(("J-001", "approved", false));
        var dog = Dog;

        dog.Tick();
        Clock.Advance(TimeSpan.FromSeconds(9));
        dog.Tick();
        Clock.Advance(TimeSpan.FromSeconds(1));
        dog.Tick();

        Launcher.Received(2).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "runner" })));
    }

    [Fact]
    public void Pr_sync_runs_sync_prs_detached_only_while_a_ready_job_has_a_pr()
    {
        SetBoard(("J-001", "ready", false), ("J-002", "done", true));
        var dog = Dog;
        dog.Tick();
        Launcher.DidNotReceive().StartDetached(Arg.Any<IReadOnlyList<string>>());

        SetBoard(("J-001", "ready", true));
        Clock.Advance(TimeSpan.FromSeconds(30));
        dog.Tick();
        Launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "sync-prs" })));
    }

    [Fact]
    public void A_broken_board_is_a_warning_not_a_crash()
    {
        Files.Put(Path.Combine(Home, "board.json"), "{not json");

        Dog.Tick();

        Launcher.DidNotReceive().StartDetached(Arg.Is<IReadOnlyList<string>>(a => a[0] == "runner" || a[0] == "idea-turn"));
    }
}
