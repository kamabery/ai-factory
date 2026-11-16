using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Data.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Factory.UnitTests;

public class DatabaseStoreTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "db-home");
    private static readonly string BoardPath = Path.Combine(Home, "board.json");

    private readonly MemoryFiles _files = new();
    private readonly IBoardRows _rows = Substitute.For<IBoardRows>();
    private readonly IStateExport _export = Substitute.For<IStateExport>();
    private readonly IStateSync _sync = Substitute.For<IStateSync>();
    private readonly IStateSettings _settings = Substitute.For<IStateSettings>();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-16T10:00:00Z", CultureInfo.InvariantCulture));

    private DatabaseStore Store()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.BoardPath.Returns(BoardPath);
        return new DatabaseStore(paths, _files, _rows, _export, _sync, _settings, _clock);
    }

    private static BoardRead Read(string stage) =>
        new(Board.Parse($$"""{"version":2,"ideas":[],"jobs":[{"id":"J-001","stage":"{{stage}}"}]}"""), "{}", 1,
            new Dictionary<string, RowRead>(), new Dictionary<string, RowRead>());

    [Fact]
    public void An_empty_database_is_the_empty_board()
    {
        using var s = Store().Open();

        Assert.Equal(Board.Empty().ToJsonString(), s.Board.ToJsonString());
    }

    [Fact]
    public void An_empty_database_beside_a_board_json_refuses_until_state_import()
    {
        _files.Put(BoardPath, "{}");

        var e = Assert.Throws<FactoryException>(() => Store().Open());

        Assert.Equal($"state: the database has no board but {BoardPath} exists; run factory state-import", e.Message);
        using var s = Store().Open(refresh: false);
        Assert.Equal(Board.Empty().ToJsonString(), s.Board.ToJsonString());
    }

    [Fact]
    public void A_save_writes_what_was_read_and_saves_again_on_what_it_returned_then_exports()
    {
        var first = Read("backlog");
        var second = first with { Version = 2 };
        _rows.Read().Returns(first);
        _rows.Save(first, first.Board).Returns(second);
        _rows.Save(second, first.Board).Returns(second);
        using var s = Store().Open();

        s.Board.Job("J-001").Node["stage"] = "approved";
        s.Save();
        s.Save();

        Received.InOrder(() =>
        {
            _rows.Save(first, first.Board);
            _export.Export();
            _rows.Save(second, first.Board);
            _export.Export();
        });
        Assert.Same(first.Board, s.Board);
    }

    [Fact]
    public void An_event_is_inserted_as_its_compact_line_then_exported()
    {
        using var s = Store().Open();

        s.AddEvent(new JsonObject { ["ts"] = "2026-11-14T10:00:00Z", ["kind"] = "note", ["detail"] = "a \"quote\"" });

        Received.InOrder(() =>
        {
            _rows.AddEvent("""{"ts":"2026-11-14T10:00:00Z","kind":"note","detail":"a \"quote\""}""");
            _export.Export();
        });
    }

    [Fact]
    public void A_long_step_runs_then_the_board_is_read_again_even_when_the_step_throws()
    {
        _rows.Read().Returns(Read("build"), Read("review"), Read("ready"));
        using var s = Store().Open();
        var ran = false;

        s.Unlocked(() => ran = true);
        Assert.True(ran);
        Assert.Equal("review", s.Board.Job("J-001").Stage);

        Assert.Throws<InvalidOperationException>(() => s.Unlocked(() => throw new InvalidOperationException()));
        Assert.Equal("ready", s.Board.Job("J-001").Stage);
    }

    [Fact]
    public void Closing_exports_and_reports_nothing()
    {
        using var s = Store().Open();

        Assert.Empty(s.Close());

        _export.Received(1).Export();
    }
    [Fact]
    public void Without_shared_files_nothing_is_pulled_or_pushed()
    {
        using (var s = Store().Open()) s.Close();

        _sync.DidNotReceiveWithAnyArgs().Pull(default!);
        _sync.DidNotReceiveWithAnyArgs().Push(default);
    }

    [Fact]
    public void With_shared_files_a_command_pulls_the_container_when_it_opens_and_pushes_when_it_closes_holding_back_live_run_logs()
    {
        _settings.SharesFiles.Returns(true);
        _rows.Read().Returns(Read("build"));
        Func<string, bool>? skip = null;
        _sync.Push(Arg.Do<Func<string, bool>?>(f => skip = f)).Returns(0);

        using var s = Store().Open();
        _sync.Received(1).Pull("");

        Assert.Empty(s.Close());
        Assert.True(skip!("jobs/J-001/run-1.jsonl"));
        Assert.False(skip("jobs/J-002/run-1.jsonl"));
        Assert.False(skip("jobs/J-001/spec.md"));
        _sync.DidNotReceiveWithAnyArgs().Prune(default!);
    }

    [Fact]
    public void A_push_that_uploaded_runs_the_cap_and_notes_what_it_pruned()
    {
        _settings.SharesFiles.Returns(true);
        _sync.Push(Arg.Any<Func<string, bool>?>()).Returns(2);
        _sync.Prune(Arg.Any<IEnumerable<Job>>()).Returns(new PruneResult(10, 5, 3, 4096));
        using var s = Store().Open();

        Assert.Empty(s.Close());

        _rows.Received(1).AddEvent("""{"ts":"2026-11-16T10:00:00.000Z","kind":"note","detail":"pruned 3 blobs, 4096 bytes freed"}""");
    }

    [Fact]
    public void A_failed_push_is_reported_not_thrown_and_state_import_does_not_pull()
    {
        _settings.SharesFiles.Returns(true);
        _sync.Push(Arg.Any<Func<string, bool>?>()).Returns(_ => throw new FactoryException("blob 403"));
        using var s = Store().Open(refresh: false);

        Assert.Equal(["factory: state push failed: blob 403"], s.Close());
        _sync.DidNotReceiveWithAnyArgs().Pull(default!);
    }
}
