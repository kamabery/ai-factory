using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Data.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class DatabaseStoreTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "db-home");
    private static readonly string BoardPath = Path.Combine(Home, "board.json");

    private readonly MemoryFiles _files = new();
    private readonly IBoardRows _rows = Substitute.For<IBoardRows>();
    private readonly IStateExport _export = Substitute.For<IStateExport>();

    private DatabaseStore Store()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.BoardPath.Returns(BoardPath);
        return new DatabaseStore(paths, _files, _rows, _export);
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
}
