using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

public class FactoryStoreTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "store-home");
    private readonly MemoryFiles _files = new();
    private readonly IBoardMutex _mutex = Substitute.For<IBoardMutex>();
    private readonly IStateSettings _settings = Substitute.For<IStateSettings>();
    private readonly IBlobClient _blobs = Substitute.For<IBlobClient>();
    private readonly IStateSync _sync = Substitute.For<IStateSync>();
    private readonly StateOptions _options = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero));

    public FactoryStoreTests()
    {
        _mutex.Enter(Arg.Any<int>()).Returns(true);
        _blobs.AcquireLease("board", Arg.Any<int>()).Returns("lease-1");
        _blobs.RenewLease("board", "lease-1").Returns(true);
    }

    private FactoryStore Store(bool azure = false)
    {
        _settings.IsAzure.Returns(azure);
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.BoardPath.Returns(Path.Combine(Home, "board.json"));
        paths.EventsPath.Returns(Path.Combine(Home, "events.jsonl"));
        return new FactoryStore(paths, _files, _mutex, _settings, Options.Create(_options), _blobs, _sync, _clock);
    }

    private static string Board(params string[] stages) =>
        new JsonObject { ["version"] = 2, ["ideas"] = new JsonArray(), ["jobs"] = new JsonArray([.. stages.Select((s, i) => (JsonNode)new JsonObject { ["id"] = $"J-00{i + 1}", ["stage"] = s })]) }.ToJsonString();

    [Fact]
    public void The_tenant_is_default() => Assert.Equal("default", Store().TenantId);

    [Fact]
    public void Creates_the_state_folder_as_factory_ps1_does_before_it_locks()
    {
        _mutex.Enter(Arg.Any<int>()).Returns(false);

        Assert.Throws<FactoryException>(() => Store().Open());

        Assert.Equal([Home], _files.Directories);
    }

    [Fact]
    public void Local_takes_the_mutex_for_30_s_and_reads_the_board_or_the_empty_one()
    {
        using (var s = Store().Open()) Assert.Equal(Factory.Core.Board.Empty().ToJsonString(), s.Board.ToJsonString());

        _files.Put(Path.Combine(Home, "board.json"), "{\"version\":2,\"jobs\":[]}");
        using (var s = Store().Open()) Assert.Equal("{\"version\":2,\"jobs\":[],\"ideas\":[]}", s.Board.ToJsonString());

        _mutex.Received(2).Enter(30);
        _blobs.DidNotReceiveWithAnyArgs().AcquireLease(default!, default);
        _sync.DidNotReceiveWithAnyArgs().SyncBoard();
    }

    [Fact]
    public void A_mutex_another_command_holds_past_the_wait_refuses()
    {
        _options.BoardLockSeconds = "2";
        _mutex.Enter(2).Returns(false);

        var e = Assert.Throws<FactoryException>(() => Store().Open());

        Assert.Equal("board lock refused: another factory.ps1 has held the board for over 2 s", e.Message);
        _mutex.DidNotReceive().Exit();
    }

    [Fact]
    public void Azure_also_takes_the_board_lease_and_a_lease_held_elsewhere_refuses_and_frees_the_mutex()
    {
        _options.StateLockSeconds = "1";
        _blobs.AcquireLease("board", 1).Returns((string?)null);

        var e = Assert.Throws<FactoryException>(() => Store(azure: true).Open());

        Assert.Equal("state lock refused: another machine has held the board (locks/board) for over 1 s", e.Message);
        _mutex.Received(1).Exit();
    }

    [Fact]
    public void Azure_refreshes_the_board_and_pulls_the_container_only_when_the_board_changed()
    {
        _sync.SyncBoard().Returns(BoardSync.Changed, BoardSync.Same);

        Store(azure: true).Open().Dispose();
        Store(azure: true).Open().Dispose();

        _blobs.Received(2).AcquireLease("board", 30);
        _sync.Received(1).Pull("");
    }

    [Fact]
    public void A_refused_refresh_releases_the_lease_and_the_mutex()
    {
        _sync.SyncBoard().Throws(new FactoryException("state: board.json is gone"));

        Assert.Throws<FactoryException>(() => Store(azure: true).Open());

        _blobs.Received(1).ReleaseLease("board", "lease-1");
        _mutex.Received(1).Exit();
    }

    [Fact]
    public void Save_writes_board_json_atomically_as_ConvertTo_Json_does()
    {
        using var s = Store().Open();
        s.Board["jobs"]!.AsArray().Add(new JsonObject { ["id"] = "J-001" });

        s.Save();

        var nl = Environment.NewLine;
        Assert.Equal($"{{{nl}  \"version\": 2,{nl}  \"ideas\": [],{nl}  \"jobs\": [{nl}    {{{nl}      \"id\": \"J-001\"{nl}    }}{nl}  ]{nl}}}",
            _files.Text(Path.Combine(Home, "board.json")));
        Assert.Contains(Path.Combine(Home, "board.json"), _files.Writes);
    }

    [Fact]
    public void Azure_save_renews_the_lease_first_then_writes_through_the_container()
    {
        using var s = Store(azure: true).Open();

        s.Save();

        Received.InOrder(() =>
        {
            _blobs.RenewLease("board", "lease-1");
            _sync.SaveBoard(Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b).StartsWith("{")));
        });
    }

    [Fact]
    public void Azure_save_refuses_when_the_lease_lapsed_and_another_machine_took_it()
    {
        _blobs.RenewLease("board", "lease-1").Returns(false);
        using var s = Store(azure: true).Open();

        Assert.Equal("state lock refused: the board lease lapsed and another machine took it",
            Assert.Throws<FactoryException>(s.Save).Message);
        _sync.DidNotReceiveWithAnyArgs().SaveBoard(default!);
    }

    [Fact]
    public void AddEvent_appends_a_compact_line_and_in_azure_the_months_blob()
    {
        using var s = Store(azure: true).Open();

        s.AddEvent(new JsonObject { ["ts"] = "t", ["kind"] = "note", ["detail"] = "é" });

        Assert.Equal("{\"ts\":\"t\",\"kind\":\"note\",\"detail\":\"é\"}\n", _files.Text(Path.Combine(Home, "events.jsonl")));
        _sync.Received(1).AppendEvent("{\"ts\":\"t\",\"kind\":\"note\",\"detail\":\"é\"}");
    }

    [Fact]
    public void Close_in_azure_pushes_holding_back_a_building_jobs_run_logs()
    {
        _files.Put(Path.Combine(Home, "board.json"), Board("build", "done"));
        Func<string, bool>? skip = null;
        _sync.Push(Arg.Do<Func<string, bool>?>(f => skip = f)).Returns(0);
        using var s = Store(azure: true).Open();

        Assert.Empty(s.Close());

        Assert.True(skip!("jobs/J-001/run-1.jsonl"));
        Assert.False(skip("jobs/J-002/run-1.jsonl"));
        Assert.False(skip("jobs/J-001/round-1/build.md"));
        _sync.DidNotReceiveWithAnyArgs().Prune(default!);
    }

    [Fact]
    public void Close_prunes_after_a_push_that_uploaded_and_notes_what_it_freed()
    {
        _sync.Push(Arg.Any<Func<string, bool>?>()).Returns(2);
        _sync.Prune(Arg.Any<IEnumerable<Job>>()).Returns(new PruneResult(10, 20, 3, 4096));
        using var s = Store(azure: true).Open();

        s.Close();

        const string line = "{\"ts\":\"2026-11-10T09:30:00.000Z\",\"kind\":\"note\",\"detail\":\"pruned 3 blobs, 4096 bytes freed\"}";
        Assert.Equal(line + "\n", _files.Text(Path.Combine(Home, "events.jsonl")));
        _sync.Received(1).AppendEvent(line);
    }

    [Fact]
    public void A_failed_push_is_reported_and_does_not_fail_the_command()
    {
        _sync.Push(Arg.Any<Func<string, bool>?>()).Throws(new FactoryException("state: PUT x answered 500"));
        using var s = Store(azure: true).Open();

        Assert.Equal(["factory: state push failed: state: PUT x answered 500"], s.Close());
    }

    [Fact]
    public void Close_is_nothing_on_the_local_backend()
    {
        using var s = Store().Open();

        Assert.Empty(s.Close());
        _sync.DidNotReceiveWithAnyArgs().Push(default);
    }

    [Fact]
    public void Dispose_releases_the_lease_then_the_mutex_once()
    {
        _blobs.When(b => b.ReleaseLease("board", "lease-1")).Throw(new FactoryException("state: release failed"));
        var s = Store(azure: true).Open();

        s.Dispose();
        s.Dispose();

        Received.InOrder(() =>
        {
            _blobs.ReleaseLease("board", "lease-1");
            _mutex.Exit();
        });
        _mutex.Received(1).Exit();
    }
}
