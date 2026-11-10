using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Factory.UnitTests;

public class StateSyncTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "sync-home");
    private readonly MemoryFiles _files = new();
    private readonly IBlobClient _blobs = Substitute.For<IBlobClient>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 30, 23, 0, 0, TimeSpan.Zero));
    private readonly StateSync _sut;

    public StateSyncTests()
    {
        _sut = NewSync();
        _blobs.Write(default!, default!).ReturnsForAnyArgs(c => "etag-" + c.ArgAt<string>(0));
    }

    /// <summary>A sync as a new process starts it: it reads .sync.json once, then keeps it in memory.</summary>
    private StateSync NewSync()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.BoardPath.Returns(P("board.json"));
        paths.EventsPath.Returns(P("events.jsonl"));
        var settings = Substitute.For<IStateSettings>();
        settings.Cap.Returns(25);
        return new StateSync(paths, _files, _blobs, settings, _clock);
    }

    private static string P(string rel) => Path.Combine(Home, rel.Replace('/', Path.DirectorySeparatorChar));

    private JsonObject Manifest() => JsonNode.Parse(_files.Text(P(".sync.json")) ?? "{}")!.AsObject();

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Push_uploads_changed_files_only_and_records_them_in_the_manifest()
    {
        _files.Put(P("ideas/I-001/idea.md"), "idea");
        _files.Put(P("board.json"), "{}");
        _files.Put(P("events.jsonl"), "{}\n");
        _files.Put(P("jobs/J-001/x.tmp"), "t");
        _files.Put(P(".local/storage-token.json"), "t");

        Assert.Equal(1, _sut.Push());
        Assert.Equal(0, _sut.Push());

        _blobs.Received(1).Write("ideas/I-001/idea.md", Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "idea"));
        _blobs.ReceivedWithAnyArgs(1).Write(default!, default!);
        var e = Manifest()["ideas/I-001/idea.md"]!;
        Assert.Equal((4L, "etag-ideas/I-001/idea.md"), ((long)e["len"]!, (string)e["etag"]!));
        Assert.Equal(_files.Stat(P("ideas/I-001/idea.md"))!.LastWriteTicks, (long)e["mtime"]!);
    }

    [Fact]
    public void Push_holds_back_what_skip_says_and_never_repushes_a_pruned_path()
    {
        _files.Put(P("jobs/J-001/run-1.jsonl"), "log");
        _files.Put(P("jobs/J-002/run-1.jsonl"), "log");
        _files.Put(P(".sync.json"), """{"jobs/J-002/run-1.jsonl":{"pruned":true}}""");

        Assert.Equal(0, _sut.Push(rel => rel.StartsWith("jobs/J-001/")));
        Assert.Equal(1, _sut.Push());

        _blobs.Received(1).Write("jobs/J-001/run-1.jsonl", Arg.Any<byte[]>());
        _blobs.DidNotReceive().Write("jobs/J-002/run-1.jsonl", Arg.Any<byte[]>());
    }

    [Fact]
    public void Push_deletes_the_blob_of_a_file_gone_locally_but_not_a_pruned_ones()
    {
        _files.Put(P(".sync.json"), """
            {"jobs/J-001/spec.md":{"len":1,"mtime":1,"etag":"a"},"jobs/J-002/spec.md":{"pruned":true},
             "board.json":{"len":1,"mtime":1,"etag":"b"},"events/":{"etag":"listed"}}
            """);

        _sut.Push();

        _blobs.Received(1).Delete("jobs/J-001/spec.md");
        _blobs.ReceivedWithAnyArgs(1).Delete(default!);
        Assert.Equal(["board.json", "events/"], Manifest().Select(kv => kv.Key));
    }

    [Fact]
    public void Pull_downloads_changed_blobs_but_local_changes_win_and_unsynced_local_files_are_kept()
    {
        _files.Put(P("a.md"), "mine, synced");
        _files.Put(P("b.md"), "mine, changed since");
        _files.Put(P("c.md"), "mine, never synced");
        var a = _files.Stat(P("a.md"))!;
        _files.Put(P(".sync.json"), $$$"""
            {"a.md":{"len":{{{a.Length}}},"mtime":{{{a.LastWriteTicks}}},"etag":"a1"},"b.md":{"len":1,"mtime":1,"etag":"b1"},
             "same.md":{"len":1,"mtime":1,"etag":"s1"}}
            """);
        _blobs.List("").Returns([Item("a.md", "a2"), Item("b.md", "b2"), Item("c.md", "c2"), Item("same.md", "s1"), Item("new.md", "n1"), Item("locks/board", "l")]);
        _blobs.Read("a.md").Returns(new BlobRead(false, "a2", B("theirs a")));
        _blobs.Read("new.md").Returns(new BlobRead(false, "n1", B("theirs new")));

        Assert.Equal(2, _sut.Pull());

        Assert.Equal(("theirs a", "mine, changed since", "mine, never synced", "theirs new"),
            (_files.Text(P("a.md")), _files.Text(P("b.md")), _files.Text(P("c.md")), _files.Text(P("new.md"))));
        Assert.Equal("n1", (string)Manifest()["new.md"]!["etag"]!);
        Assert.Equal(_files.Stat(P("new.md"))!.LastWriteTicks, (long)Manifest()["new.md"]!["mtime"]!);
    }

    private static BlobItem Item(string name, string etag) => new(name, null, true, 1, DateTime.UnixEpoch, etag, false);

    [Fact]
    public void SyncBoard_refreshes_a_changed_board_and_answers_same_for_its_etag()
    {
        _blobs.Read("board.json", null).Returns(new BlobRead(false, "b1", B("{\"version\":2}")));
        _blobs.Read("board.json", "b1").Returns(new BlobRead(true, "b1", null));

        Assert.Equal(BoardSync.Changed, _sut.SyncBoard());
        Assert.Equal(BoardSync.Same, _sut.SyncBoard());

        Assert.Equal("{\"version\":2}", _files.Text(P("board.json")));
        Assert.Equal("b1", (string)Manifest()["board.json"]!["etag"]!);
    }

    [Fact]
    public void SyncBoard_with_no_board_anywhere_is_none()
    {
        Assert.Equal(BoardSync.None, _sut.SyncBoard());
    }

    [Fact]
    public void SyncBoard_refuses_a_local_board_the_container_lacks_and_a_board_gone_from_the_container()
    {
        _files.Put(P("board.json"), "{}");

        Assert.Equal($"state: the container has no board.json but {P("board.json")} exists; run factory.ps1 state-migrate",
            Assert.Throws<FactoryException>(() => _sut.SyncBoard()).Message);

        _files.Put(P(".sync.json"), """{"board.json":{"len":2,"mtime":1,"etag":"b1"}}""");
        Assert.Equal("state: board.json is gone from the container; restore a version of it, or point FACTORY_BLOB_URL at the right container",
            Assert.Throws<FactoryException>(() => NewSync().SyncBoard()).Message);
    }

    [Fact]
    public void SaveBoard_writes_if_match_the_etag_it_read_or_if_none_match_for_a_new_board_then_locally()
    {
        _sut.SaveBoard(B("{1}"));
        _blobs.Received(1).Write("board.json", Arg.Any<byte[]>(), null, true);

        _sut.SaveBoard(B("{2}"));
        _blobs.Received(1).Write("board.json", Arg.Any<byte[]>(), "etag-board.json", false);
        Assert.Equal("{2}", _files.Text(P("board.json")));
    }

    [Fact]
    public void AppendEvent_appends_to_the_months_blob_and_tracks_its_length()
    {
        _blobs.Append("events/2026-11.jsonl", Arg.Any<byte[]>()).Returns(new AppendResult("e1", 0), new AppendResult("e2", 3), new AppendResult("e3", 99));

        _sut.AppendEvent("{}");
        Assert.Equal((3L, "e1"), ((long)Manifest()["events/2026-11.jsonl"]!["len"]!, (string)Manifest()["events/2026-11.jsonl"]!["etag"]!));
        _sut.AppendEvent("{}");
        Assert.Equal(6L, (long)Manifest()["events/2026-11.jsonl"]!["len"]!);
        // Another machine's block landed in between: the ETag is unknown, so a reader rebuilds.
        _sut.AppendEvent("{}");
        Assert.Equal(("", 0L), ((string)Manifest()["events/2026-11.jsonl"]!["etag"]!, (long)Manifest()["events/2026-11.jsonl"]!["len"]!));
        Assert.Equal("listed", (string)Manifest()["events/"]!["etag"]!);
        _blobs.Received(3).Append("events/2026-11.jsonl", Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "{}\n"));
    }

    [Fact]
    public void Prune_deletes_in_order_until_under_the_cap_marks_pruned_paths_and_keeps_local_files()
    {
        _files.Put(P("jobs/J-001/run-1.jsonl"), "x");
        _blobs.List("", true).Returns([
            new BlobItem("board.json", "v9", true, 10, DateTime.UnixEpoch, "b", false),
            new BlobItem("jobs/J-001/run-1.jsonl", "v1", true, 10, DateTime.UnixEpoch, "r", false),
            new BlobItem("jobs/J-001/spec.md", "v2", true, 10, DateTime.UnixEpoch, "s", false),
            new BlobItem("jobs/J-001/spec.md", "v0", false, 5, DateTime.UnixEpoch, "s", false),
        ]);

        var r = _sut.Prune([new Job(new JsonObject { ["id"] = "J-001", ["stage"] = "done", ["updatedAt"] = "2026-01-01T00:00:00.000Z" })]);

        Assert.Equal(new PruneResult(20, 25, 2, 15), r);
        Received.InOrder(() =>
        {
            _blobs.Delete("jobs/J-001/spec.md", "v0");
            _blobs.Delete("jobs/J-001/run-1.jsonl");
            _blobs.Delete("jobs/J-001/run-1.jsonl", "v1");
        });
        _blobs.DidNotReceive().Delete("jobs/J-001/spec.md");
        Assert.True((bool)Manifest()["jobs/J-001/run-1.jsonl"]!["pruned"]!);
        Assert.Equal("x", _files.Text(P("jobs/J-001/run-1.jsonl")));
    }

    [Fact]
    public void Prune_under_the_cap_deletes_nothing()
    {
        _blobs.List("", true).Returns([new BlobItem("board.json", "v9", true, 25, DateTime.UnixEpoch, "b", false)]);

        Assert.Equal(new PruneResult(25, 25, 0, 0), _sut.Prune([]));
        _blobs.DidNotReceiveWithAnyArgs().Delete(default!);
    }
}
