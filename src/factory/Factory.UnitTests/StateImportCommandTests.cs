using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Data.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class StateImportCommandTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "import-home");
    private static readonly string BoardPath = Path.Combine(Home, "board.json");
    private static readonly string EventsPath = Path.Combine(Home, "events.jsonl");

    private readonly IStateSettings _settings = Substitute.For<IStateSettings>();
    private readonly IStateSync _sync = Substitute.For<IStateSync>();
    private readonly MemoryFiles _files = new();
    private readonly IBoardRows _rows = Substitute.For<IBoardRows>();
    private readonly IStateExport _export = Substitute.For<IStateExport>();

    public StateImportCommandTests()
    {
        _settings.IsDatabase.Returns(true);
        _rows.Import(default!, default!).ReturnsForAnyArgs(c => new ImportResult(1, 2, c.Arg<IReadOnlyList<string>>().Count));
    }

    private ICommand Sut()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.BoardPath.Returns(BoardPath);
        paths.EventsPath.Returns(EventsPath);
        return new StateImportCommand(_settings, _sync, paths, _files, _rows, _export);
    }

    private CommandResult Run(params string[] args) => Sut().Run(CommandArgs.Parse(["state-import", .. args]), FakeSession.With());

    [Fact]
    public void Imports_the_board_and_every_non_blank_event_line_then_rebuilds_the_exports_whole()
    {
        _files.Put(BoardPath, """{"version":2,"jobs":[{"id":"J-001"}]}""");
        _files.Put(EventsPath, "{\"n\":1}\r\n\r\n{\"n\":2}\n");
        _files.Put(Path.Combine(Home, ".export.json"), "{\"revision\":9,\"seq\":9}");

        var r = Run();

        Assert.Equal(["imported 1 ideas, 2 jobs, 2 events", "check: board.json and events.jsonl match the database"], r.Out);
        _rows.Received(1).Import(Arg.Is<JsonObject>(b => b.ToJsonString() == """{"version":2,"jobs":[{"id":"J-001"}],"ideas":[]}"""),
            Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "{\"n\":1}", "{\"n\":2}" })));
        Assert.Null(_files.Text(Path.Combine(Home, ".export.json")));
        _export.Received(1).Export();
        _sync.DidNotReceive().Download();
    }

    [Fact]
    public void A_board_with_no_events_file_imports_no_events()
    {
        _files.Put(BoardPath, """{"version":2}""");

        Assert.Equal("imported 1 ideas, 2 jobs, 0 events", Run().Out[0]);
    }

    [Fact]
    public void Azure_downloads_the_container_first()
    {
        _settings.ContainerUrl.Returns("https://st/factory");
        _sync.When(s => s.Download()).Do(_ => _files.Put(BoardPath, """{"version":2}"""));

        Run("-Azure");

        Received.InOrder(() => { _sync.Download(); _rows.Import(Arg.Any<JsonObject>(), Arg.Any<IReadOnlyList<string>>()); });
    }

    [Fact]
    public void Refuses_off_a_database_with_no_container_or_with_no_board()
    {
        Assert.False(Sut().RefreshesBoard);
        Assert.Equal("state-import refused: FACTORY_STATE is not sqlite or sql", Refusal(false));
        Assert.Equal("state-import -Azure refused: FACTORY_BLOB_URL (the container URL) is not set", Refusal(true, "-Azure"));
        Assert.Equal($"state-import refused: there is no {BoardPath} to import", Refusal(true));
        Assert.Empty(_rows.ReceivedCalls());
    }

    private string Refusal(bool database, params string[] args)
    {
        _settings.IsDatabase.Returns(database);
        return Assert.Throws<FactoryException>(() => Run(args)).Message;
    }
}
