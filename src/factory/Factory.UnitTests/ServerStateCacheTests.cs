using Factory.Server;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>The server's refresh of polled state files (contract, "State backends", Sync, "Dashboard").</summary>
public class ServerStateCacheTests : ServerTestBase
{
    private readonly IStateExport _export = Substitute.For<IStateExport>();

    private StateCache Sut => new(State, Sync, _export, Opts, Clock, NullLogger<StateCache>.Instance);

    [Fact]
    public void Canary_json_is_pulled_with_shared_files_at_most_once_per_poll_interval()
    {
        State.SharesFiles.Returns(true);
        State.IsAzure.Returns(true);
        Options.StatePollSeconds = 5;
        var sut = Sut;

        sut.Canary();
        sut.Canary();
        Clock.Advance(TimeSpan.FromSeconds(6));
        sut.Canary();

        Sync.Received(2).Pull("canary.json");
    }

    [Fact]
    public void Canary_json_is_never_pulled_without_shared_files()
    {
        State.IsDatabase.Returns(true);
        Sut.Canary();
        Sync.DidNotReceiveWithAnyArgs().Pull(default!);
    }
}
