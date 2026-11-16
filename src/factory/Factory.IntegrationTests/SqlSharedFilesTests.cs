using Factory.Core;
using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary>
/// sql with FACTORY_BLOB_URL, as a deployed factory runs (contract, "State backends", Database): the board in SQL Server,
/// the artifacts shared through a container of the blob stub, two homes standing in for two containers.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlSharedFilesTests(SqlServer server) : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly BlobStub _blobs = new();
    private readonly string _sql = server.NewDatabase();
    private string _container = "";

    public async ValueTask InitializeAsync()
    {
        await _blobs.InitializeAsync();
        _container = _blobs.NewContainer();
    }

    public async ValueTask DisposeAsync()
    {
        await _blobs.DisposeAsync();
        _dir.Dispose();
    }

    private StoreHome Home(string name) => new(_dir[name], new StateOptions { State = "sql", Sql = _sql, BlobUrl = _container, Token = BlobStub.Token });

    [Fact]
    public void What_one_home_writes_reaches_the_next_command_in_another_and_the_exports_stay_out_of_the_container()
    {
        var a = Home("a");
        var b = Home("b");

        using (var s = a.Store.Open())
        {
            s.Board["seq"] = 1;
            s.Save();
            var spec = Path.Combine(a.Home, "jobs", "J-001", "spec.md");
            Directory.CreateDirectory(Path.GetDirectoryName(spec)!);
            File.WriteAllText(spec, "# the spec");
            Assert.Empty(s.Close());
        }

        using (var s = b.Store.Open()) Assert.Empty(s.Close());

        Assert.Equal("# the spec", File.ReadAllText(Path.Combine(b.Home, "jobs", "J-001", "spec.md")));
        var names = _blobs.Blobs(_container).Select(kv => kv.Key).ToList();
        Assert.Contains("jobs/J-001/spec.md", names);
        Assert.DoesNotContain("board.json", names);
        Assert.DoesNotContain(".export.json", names);
        Assert.DoesNotContain(names, n => n.StartsWith("events", StringComparison.Ordinal));
    }
}
