using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Data.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class StateExportTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "export-home");
    private static readonly string BoardPath = Path.Combine(Home, "board.json");
    private static readonly string EventsPath = Path.Combine(Home, "events.jsonl");
    private static readonly string Manifest = Path.Combine(Home, ".export.json");
    private static readonly string Lock = Path.Combine(Home, "export.lock");

    private readonly MemoryFiles _files = new();
    private readonly IBoardRows _rows = Substitute.For<IBoardRows>();
    private readonly IPause _pause = Substitute.For<IPause>();

    private StateExport Export(IFileStore? files = null)
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.BoardPath.Returns(BoardPath);
        paths.EventsPath.Returns(EventsPath);
        return new StateExport(paths, files ?? _files, _rows, _pause);
    }

    private static JsonObject OneJob(string stage) =>
        Board.Parse($$"""{"version":2,"ideas":[],"jobs":[{"id":"J-001","stage":"{{stage}}"}]}""");

    private void Database(long revision, JsonObject? board, params (long Seq, string Line)[] events)
    {
        _rows.Revision().Returns(revision);
        _rows.Read().Returns(board is null ? null : new BoardRead(board, null, 1, new Dictionary<string, RowRead>(), new Dictionary<string, RowRead>()));
        _rows.EventsAfter(Arg.Any<long>()).Returns(c => events.Where(e => e.Seq > c.Arg<long>()).Select(e => new EventLine(e.Seq, e.Line)).ToList());
    }

    [Fact]
    public void A_database_with_no_board_exports_nothing()
    {
        Database(0, null);

        Export().Export();

        Assert.Empty(_files.Writes);
        Assert.Null(_files.Text(EventsPath));
    }

    [Fact]
    public void The_first_export_writes_the_board_as_the_file_backend_does_and_rebuilds_events_whole()
    {
        _files.Put(EventsPath, "a line the import already wrote\n");
        Database(3, OneJob("backlog"), (1, "{\"kind\":\"idea\"}"), (2, "{\"kind\":\"stage\"}"));

        Export().Export();

        Assert.Equal(PsJson.Write(OneJob("backlog"), Environment.NewLine), _files.Text(BoardPath));
        Assert.Equal("{\"kind\":\"idea\"}\n{\"kind\":\"stage\"}\n", _files.Text(EventsPath));
        Assert.Equal("{\"revision\":3,\"seq\":2}", _files.Text(Manifest));
        Assert.Empty(_files.Held);
    }

    [Fact]
    public void An_export_up_to_the_revision_reads_nothing_more()
    {
        Database(3, OneJob("backlog"), (1, "{}"));
        Export().Export();
        _rows.ClearReceivedCalls();

        Export().Export();

        _rows.Received(1).Revision();
        _rows.DidNotReceive().Read();
        _rows.DidNotReceive().EventsAfter(Arg.Any<long>());
    }

    [Fact]
    public void A_later_revision_appends_only_the_new_events_and_leaves_an_unchanged_board_alone()
    {
        Database(3, OneJob("backlog"), (1, "{\"n\":1}"));
        Export().Export();
        _files.Writes.Clear();
        Database(4, OneJob("backlog"), (1, "{\"n\":1}"), (2, "{\"n\":2}"));

        Export().Export();

        Assert.Equal("{\"n\":1}\n{\"n\":2}\n", _files.Text(EventsPath));
        Assert.DoesNotContain(BoardPath, _files.Writes);
        Assert.Equal("{\"revision\":4,\"seq\":2}", _files.Text(Manifest));
    }

    [Fact]
    public void A_changed_board_is_rewritten()
    {
        Database(3, OneJob("backlog"));
        Export().Export();
        Database(4, OneJob("approved"));

        Export().Export();

        Assert.Equal(PsJson.Write(OneJob("approved"), Environment.NewLine), _files.Text(BoardPath));
        Assert.Equal("{\"revision\":4,\"seq\":0}", _files.Text(Manifest));
    }

    [Fact]
    public void A_database_behind_the_manifest_was_replaced_and_events_are_rebuilt_whole()
    {
        _files.Put(Manifest, "{\"revision\":90,\"seq\":80}");
        _files.Put(EventsPath, "old\n");
        Database(2, OneJob("backlog"), (1, "{\"n\":1}"));

        Export().Export();

        Assert.Equal("{\"n\":1}\n", _files.Text(EventsPath));
    }

    [Fact]
    public void A_missing_board_json_is_written_even_at_the_manifest_s_revision()
    {
        Database(3, OneJob("backlog"));
        Export().Export();
        _files.Remove(BoardPath);

        Export().Export();

        Assert.NotNull(_files.Text(BoardPath));
    }

    [Fact]
    public void Waits_5_s_for_export_lock_then_skips_the_export()
    {
        _files.Held.Add(Lock);
        Database(3, OneJob("backlog"));

        Export().Export();

        _pause.Received(100).Sleep(TimeSpan.FromMilliseconds(50));
        _rows.DidNotReceive().Revision();
        Assert.Null(_files.Text(BoardPath));
    }

    [Fact]
    public void A_file_it_cannot_write_is_left_for_the_next_export()
    {
        var files = Substitute.For<IFileStore>();
        files.TryLock(Lock).Returns(Substitute.For<IDisposable>());
        files.When(f => f.WriteAtomic(BoardPath, Arg.Any<byte[]>())).Throw(new IOException("in use"));
        Database(3, OneJob("backlog"));

        Export(files).Export();

        files.DidNotReceive().WriteAtomic(Manifest, Arg.Any<byte[]>());
    }
}
