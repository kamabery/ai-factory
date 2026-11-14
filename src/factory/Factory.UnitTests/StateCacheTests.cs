using Factory.Server;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Factory.UnitTests;

public class StateCacheTests
{
    private readonly IStateSettings _settings = Substitute.For<IStateSettings>();
    private readonly IStateSync _sync = Substitute.For<IStateSync>();
    private readonly IStateExport _export = Substitute.For<IStateExport>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));

    private StateCache Cache() =>
        new(_settings, _sync, _export, Options.Create(new ServerOptions { StatePollSeconds = 5 }), _clock, NullLogger<StateCache>.Instance);

    [Fact]
    public void Local_reads_the_files_as_they_are()
    {
        var c = Cache();
        c.Board();
        c.Events();
        c.Job("J-001");

        Assert.Empty(_sync.ReceivedCalls());
        Assert.Empty(_export.ReceivedCalls());
    }

    [Fact]
    public void Azure_refreshes_from_the_container_at_most_once_per_poll_interval()
    {
        _settings.IsAzure.Returns(true);
        var c = Cache();

        c.Board(); c.Board(); c.Events(); c.Job("J-001");
        _clock.Advance(TimeSpan.FromSeconds(5));
        c.Board();

        _sync.Received(2).SyncBoard();
        _sync.Received(1).SyncEvents();
        _sync.Received(1).Pull("jobs/J-001/");
        Assert.Empty(_export.ReceivedCalls());
    }

    [Fact]
    public void A_database_brings_the_exports_up_to_date_at_most_once_per_poll_interval_and_pulls_no_job()
    {
        _settings.IsDatabase.Returns(true);
        var c = Cache();

        c.Board(); c.Board(); c.Events(); c.Job("J-001");
        _clock.Advance(TimeSpan.FromSeconds(5));
        c.Events();

        _export.Received(3).Export();
        Assert.Empty(_sync.ReceivedCalls());
    }

    [Fact]
    public void A_failed_refresh_is_logged_not_thrown()
    {
        _settings.IsDatabase.Returns(true);
        _export.When(e => e.Export()).Throw(new InvalidOperationException("database gone"));

        Cache().Board();
    }
}
