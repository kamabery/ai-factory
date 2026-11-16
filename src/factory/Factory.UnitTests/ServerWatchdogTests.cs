using Factory.Server;
using Factory.Server.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Factory.Store.Abstractions;
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
    public async Task The_server_starts_a_sweep_as_it_starts()
    {
        var hosted = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddFactoryServer(new Dictionary<string, string?>(), Home, Path.Combine(Home, "scripts"), Options.EnvFile, 0, 10, 30, 0, Home, Work, Home)
            .AddSingleton(Launcher)
            .AddSingleton(Substitute.For<Microsoft.Extensions.Hosting.IHostApplicationLifetime>())
            .BuildServiceProvider()
            .GetServices<Microsoft.Extensions.Hosting.IHostedService>();
        var sweep = Assert.Single(hosted.OfType<Factory.Server.StartupSweep>());

        await sweep.StartAsync(TestContext.Current.CancellationToken);
        Launcher.DidNotReceiveWithAnyArgs().StartDetached(default!);
        await sweep.StartedAsync(TestContext.Current.CancellationToken);

        Launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "sweep" })));
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

        new Factory.Server.Watchdog(Board, Paths, Files, Launcher, Runner, Leases, Factory, Procs, Opts, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<Factory.Server.Watchdog>.Instance).Tick();

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
    private void Cloud()
    {
        Runner = Substitute.For<IRunnerStarter>();
        Runner.StartsExecutions.Returns(true);
        Runner.Start().Returns("started runner execution r-1");
        Factory.Run("next", "-NeedsCanary").Returns(CallResult.Done("J-001"));
    }

    [Fact]
    public void In_the_cloud_it_starts_nothing_while_next_has_nothing_to_run()
    {
        Cloud();
        SetBoard(("J-001", "approved", false));
        Factory.Run("next", "-NeedsCanary").Returns(CallResult.Done(""));

        Dog.Tick();

        Runner.DidNotReceive().Start();
    }

    [Fact]
    public void In_the_cloud_it_starts_an_execution_when_no_runner_holds_the_lease_and_logs_its_name()
    {
        Cloud();
        SetBoard(("J-001", "approved", false));
        Files.Put(RunnerLock, "555");   // another container's lock means nothing here

        Dog.Tick();

        Runner.Received(1).Start();
        Assert.Equal($"2026-11-10T12:00:00.0000000Z watchdog: started runner execution r-1{Environment.NewLine}", Files.Text(Path.Combine(Home, "runner.log")));
        Launcher.DidNotReceiveWithAnyArgs().StartDetached(default!);
    }

    [Fact]
    public void In_the_cloud_a_held_runner_lease_or_an_execution_started_under_3_minutes_ago_counts_as_alive()
    {
        Cloud();
        SetBoard(("J-001", "approved", false));
        Leases.Held("runner").Returns(true);
        var dog = Dog;

        dog.Tick();
        Runner.DidNotReceive().Start();

        Leases.Held("runner").Returns(false);
        Clock.Advance(TimeSpan.FromSeconds(10));
        dog.Tick();
        Runner.Received(1).Start();

        Clock.Advance(TimeSpan.FromSeconds(170));   // still starting
        dog.Tick();
        Runner.Received(1).Start();

        Clock.Advance(TimeSpan.FromSeconds(10));    // 3 minutes on, and no lease: it died
        dog.Tick();
        Runner.Received(2).Start();
    }
}
