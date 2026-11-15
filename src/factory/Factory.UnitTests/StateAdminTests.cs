using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

/// <summary>state.ps1's Sync-Events, Invoke-StateMigrate and Invoke-StateAllowIp, ported for spec 11.</summary>
public class StateAdminTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "admin-home");
    private readonly MemoryFiles _files = new();
    private readonly IBlobClient _blobs = Substitute.For<IBlobClient>();
    private readonly IStateSettings _settings = Substitute.For<IStateSettings>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 30, 23, 0, 0, TimeSpan.Zero));

    public StateAdminTests()
    {
        _settings.IsAzure.Returns(true);
        _settings.Cap.Returns(1L << 30);
        _blobs.Write(default!, default!).ReturnsForAnyArgs(c => "etag-" + c.ArgAt<string>(0));
        _blobs.Append(default!, default!).ReturnsForAnyArgs(c => new AppendResult("etag-" + c.ArgAt<string>(0), 0));
    }

    private StateSync Sync()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.BoardPath.Returns(P("board.json"));
        paths.EventsPath.Returns(P("events.jsonl"));
        return new StateSync(paths, _files, _blobs, _settings, _clock);
    }

    private static string P(string rel) => Path.Combine(Home, rel.Replace('/', Path.DirectorySeparatorChar));

    private JsonObject Manifest() => JsonNode.Parse(_files.Text(P(".sync.json")) ?? "{}")!.AsObject();

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static BlobItem Item(string name) => new(name, null, true, 1, DateTime.UtcNow, "e", false);

    // --- Sync-Events -----------------------------------------------------------------------------

    [Fact]
    public void Sync_events_rebuilds_events_jsonl_from_the_month_blobs_in_order_on_a_cache_that_never_listed_them()
    {
        _blobs.List("events/").Returns([Item("events/2026-11.jsonl"), Item("events/2026-10.jsonl")]);
        _blobs.Read("events/2026-10.jsonl").Returns(new BlobRead(false, "e10", B("a\n")));
        _blobs.Read("events/2026-11.jsonl").Returns(new BlobRead(false, "e11", B("b\n")));

        Assert.True(Sync().SyncEvents());

        Assert.Equal("a\nb\n", _files.Text(P("events.jsonl")));
        var m = Manifest();
        Assert.Equal(("listed", "e10", 2L), ((string?)m["events/"]!["etag"], (string?)m["events/2026-10.jsonl"]!["etag"], (long)m["events/2026-10.jsonl"]!["len"]!));
    }

    [Fact]
    public void Sync_events_does_nothing_while_this_months_blob_is_unchanged_or_off_azure()
    {
        _files.Put(P(".sync.json"), """{"events/":{"etag":"listed"},"events/2026-11.jsonl":{"etag":"e11","len":2}}""");
        _blobs.Read("events/2026-11.jsonl", "e11").Returns(new BlobRead(true, "e11", null));
        Assert.False(Sync().SyncEvents());
        _blobs.DidNotReceive().List(Arg.Any<string>(), Arg.Any<bool>());

        _settings.IsAzure.Returns(false);
        Assert.False(Sync().SyncEvents());
    }

    [Fact]
    public void Sync_events_with_no_month_blobs_only_records_that_it_listed()
    {
        _blobs.List("events/").Returns([]);

        Assert.False(Sync().SyncEvents());

        Assert.Equal("listed", (string?)Manifest()["events/"]!["etag"]);
        Assert.Null(_files.Text(P("events.jsonl")));
    }

    // --- Invoke-StateMigrate ---------------------------------------------------------------------

    [Fact]
    public void Migrate_uploads_events_by_month_then_the_files_then_the_board_last()
    {
        _files.Put(P("events.jsonl"), "{\"ts\":\"2026-10-01T00:00:00.000Z\"}\r\n\n{\"ts\":\"2026-11-02T00:00:00.000Z\"}\n{\"kind\":\"note\"}\n{\"ts\":\"2026-10-03T00:00:00.000Z\"}\n");
        _files.Put(P("ideas/I-001/idea.md"), "idea");
        _files.Put(P("board.json"), "{\"version\":2}");
        var order = new List<string>();
        _blobs.When(b => b.Append(Arg.Any<string>(), Arg.Any<byte[]>())).Do(c => order.Add("append " + c.ArgAt<string>(0)));
        _blobs.When(b => b.Write(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string?>(), Arg.Any<bool>())).Do(c => order.Add("write " + c.ArgAt<string>(0)));

        Assert.Equal(4, Sync().Migrate());

        Assert.Equal(["append events/2026-10.jsonl", "append events/2026-11.jsonl", "write ideas/I-001/idea.md", "write board.json"], order);
        _blobs.Received(1).Append("events/2026-10.jsonl", Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "{\"ts\":\"2026-10-01T00:00:00.000Z\"}\n{\"ts\":\"2026-10-03T00:00:00.000Z\"}\n"));
        _blobs.Received(1).Append("events/2026-11.jsonl", Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b).EndsWith("{\"kind\":\"note\"}\n")));
        _blobs.Received(1).Delete("events/2026-10.jsonl");
        _blobs.Received(1).Write("board.json", Arg.Any<byte[]>(), null, true);
        Assert.Equal(("listed", 68L), ((string?)Manifest()["events/"]!["etag"], (long)Manifest()["events/2026-10.jsonl"]!["len"]!));
    }

    [Fact]
    public void Migrate_appends_a_month_over_4_MiB_in_4_MiB_blocks_and_works_with_no_events_or_board()
    {
        var line = "{\"ts\":\"2026-11-01T00:00:00.000Z\",\"x\":\"" + new string('x', 1000) + "\"}";
        _files.Put(P("events.jsonl"), string.Concat(Enumerable.Repeat(line + "\n", 5000)));
        Assert.Equal(1, Sync().Migrate());
        _blobs.Received(2).Append("events/2026-11.jsonl", Arg.Any<byte[]>());

        _files.Remove(P("events.jsonl"));
        _files.Remove(P(".sync.json"));
        Assert.Equal(0, Sync().Migrate());
    }

    [Fact]
    public void Migrate_refuses_a_container_that_has_a_board_already()
    {
        _blobs.Read("board.json").Returns(new BlobRead(false, "e", B("{}")));

        Assert.Equal("state-migrate refused: the container already has a board.json", Assert.Throws<FactoryException>(() => Sync().Migrate()).Message);
        _blobs.DidNotReceiveWithAnyArgs().Append(default!, default!);
    }

    // --- Invoke-StateAllowIp ---------------------------------------------------------------------

    private readonly IProcessRunner _proc = Substitute.For<IProcessRunner>();
    private readonly StubHttpHandler _http = new();
    private static readonly string LocalDir = Path.Combine(Path.GetTempPath(), "admin-local");

    private StateFirewall Firewall(StateOptions? o = null) =>
        new(new HttpClient(_http), _proc, _files, _settings, Options.Create(o ?? new StateOptions { LocalDir = LocalDir }));

    private List<string> AzCalls() => [.. _proc.ReceivedCalls().Select(c => string.Join(' ', (IReadOnlyList<string>)c.GetArguments()[1]!))];

    [Fact]
    public void Allow_ip_adds_this_ip_removes_the_last_and_records_it()
    {
        _settings.Account.Returns("stacct");
        _settings.Group.Returns("rg-x");
        _proc.Run(default!, default!, default!).ReturnsForAnyArgs(new ProcessResult(0, "", ""));
        _files.Put(Path.Combine(LocalDir, "allowed-ip.txt"), "203.0.113.7\r\n");
        _http.Respond = r => r.RequestUri!.Host == "ip.test" ? " 198.51.100.9\n" : "";

        var r = Firewall(new StateOptions { LocalDir = LocalDir, IpUrl = "http://ip.test/", AzCmd = "/fakes/az" }).AllowIp();

        Assert.Equal(new AllowIpResult("198.51.100.9", "203.0.113.7", "stacct"), r);
        Assert.Equal([
            "storage account network-rule add --account-name stacct --resource-group rg-x --ip-address 198.51.100.9 --output none",
            "storage account network-rule remove --account-name stacct --resource-group rg-x --ip-address 203.0.113.7 --output none",
        ], AzCalls());
        Assert.All(_proc.ReceivedCalls(), c => Assert.Equal("/fakes/az", (string)c.GetArguments()[0]!));
        Assert.Equal("198.51.100.9", _files.Text(Path.Combine(LocalDir, "allowed-ip.txt"))!.Trim());
    }

    [Fact]
    public void Allow_ip_with_the_same_ip_as_last_time_changes_nothing_and_with_none_removes_nothing()
    {
        _settings.Account.Returns("stacct");
        _http.Respond = _ => "203.0.113.7";
        _files.Put(Path.Combine(LocalDir, "allowed-ip.txt"), "203.0.113.7");
        Assert.Equal(new AllowIpResult("203.0.113.7", null, "stacct"), Firewall().AllowIp());
        Assert.Empty(_proc.ReceivedCalls());

        _files.Remove(Path.Combine(LocalDir, "allowed-ip.txt"));
        _proc.Run(default!, default!, default!).ReturnsForAnyArgs(new ProcessResult(0, "", ""));
        Assert.Equal(new AllowIpResult("203.0.113.7", null, "stacct"), Firewall().AllowIp());
        Assert.Single(AzCalls());
        Assert.Equal(StateToken.AzProgram, (string)_proc.ReceivedCalls().Single().GetArguments()[0]!);
    }

    [Fact]
    public void Allow_ip_refuses_a_bad_answer_a_failed_az_and_keeps_going_when_only_the_removal_fails()
    {
        _settings.Account.Returns("stacct");
        _settings.Group.Returns("rg");
        _http.Respond = _ => "<html>";
        Assert.Equal("state: https://api.ipify.org answered '<html>', not an IPv4 address", Assert.Throws<FactoryException>(() => Firewall().AllowIp()).Message);

        _http.Respond = _ => "198.51.100.9";
        _proc.Run(default!, default!, default!).ReturnsForAnyArgs(new ProcessResult(1, "", "denied"));
        Assert.Equal("state: az storage account network-rule add failed: denied", Assert.Throws<FactoryException>(() => Firewall().AllowIp()).Message);

        _files.Put(Path.Combine(LocalDir, "allowed-ip.txt"), "203.0.113.7");
        _proc.Run(default!, default!, default!).ReturnsForAnyArgs(c => ((IReadOnlyList<string>)c.ArgAt<IReadOnlyList<string>>(1)).Contains("add") ? new ProcessResult(0, "", "") : new ProcessResult(1, "", "gone"));
        Assert.Equal("203.0.113.7", Firewall().AllowIp().Removed);
    }

    [Fact]
    public void Allow_ip_refuses_without_an_account_or_an_answer()
    {
        _settings.Account.Returns((string?)null);
        Assert.Equal("state: allow-ip needs FACTORY_STORAGE_ACCOUNT or FACTORY_BLOB_URL", Assert.Throws<FactoryException>(() => Firewall().AllowIp()).Message);

        _settings.Account.Returns("stacct");
        _http.Reply = _ => throw new HttpRequestException("no route");
        Assert.Equal("state: https://api.ipify.org did not answer: no route", Assert.Throws<FactoryException>(() => Firewall().AllowIp()).Message);
    }

    // --- settings and the store ------------------------------------------------------------------

    [Fact]
    public void The_account_is_FACTORY_STORAGE_ACCOUNT_else_the_container_hosts_first_label_and_the_group_has_a_default()
    {
        var dotEnv = Substitute.For<IDotEnv>();
        dotEnv.Get(Arg.Any<string>()).Returns((string?)null);
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        StateSettings S(StateOptions o) => new(Options.Create(o), paths, dotEnv);

        var url = S(new StateOptions { State = "azure", BlobUrl = "https://stfactory.blob.core.windows.net/state" });
        Assert.Equal(("stfactory", "rg-factory-state"), (url.Account, url.Group));
        Assert.Equal(("acct", "rg-own"), (S(new StateOptions { StorageAccount = "acct", StateGroup = "rg-own" }).Account, S(new StateOptions { StateGroup = "rg-own" }).Group));
        Assert.Null(S(new StateOptions()).Account);
        dotEnv.Get("FACTORY_STORAGE_ACCOUNT").Returns("fromenv");
        Assert.Equal("fromenv", S(new StateOptions()).Account);
    }
}
