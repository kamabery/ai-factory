using Factory.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class ServerParentWatchTests : ServerTestBase
{
    private readonly IHostApplicationLifetime _lifetime = Substitute.For<IHostApplicationLifetime>();
    private static readonly DateTime Started = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private ParentWatch Watch(int parent)
    {
        Options.ParentPid = parent;
        return new ParentWatch(Opts, Procs, _lifetime, Clock, NullLogger<ParentWatch>.Instance);
    }

    [Fact]
    public void Started_directly_with_no_parent_it_never_stops_the_server()
    {
        Assert.False(Watch(0).Check());
        _lifetime.DidNotReceive().StopApplication();
    }

    [Fact]
    public void Keeps_serving_while_serve_ps1_runs_and_stops_once_it_is_gone()
    {
        Procs.StartTime(900).Returns(Started);
        var watch = Watch(900);

        Assert.False(watch.Check());
        Assert.False(watch.Check());
        _lifetime.DidNotReceive().StopApplication();

        Procs.StartTime(900).Returns((DateTime?)null);
        Assert.True(watch.Check());
        _lifetime.Received(1).StopApplication();
    }

    [Fact]
    public void A_reused_pid_is_not_the_parent()
    {
        Procs.StartTime(900).Returns(Started);
        var watch = Watch(900);
        watch.Check();

        Procs.StartTime(900).Returns(Started.AddMinutes(3));

        Assert.True(watch.Check());
        _lifetime.Received(1).StopApplication();
    }

    [Fact]
    public void A_parent_already_gone_when_the_watch_begins_stops_the_server_at_once()
    {
        Procs.StartTime(900).Returns((DateTime?)null);

        Assert.True(Watch(900).Check());
        _lifetime.Received(1).StopApplication();
    }
}
