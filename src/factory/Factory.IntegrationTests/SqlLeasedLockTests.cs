using Factory.Data;
using Factory.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>
/// runner.lock, canary.lock and turn.lock in sql (contract, "State backends", Database, Locks): the file and a Leases row
/// together. Two homes on one SQL Server database stand in for two machines.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlLeasedLockTests(SqlServer server) : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _sql = server.NewDatabase();

    public void Dispose() => _dir.Dispose();

    private (LeasedLock Lock, string File) Lock(string home, string renewSeconds = "20")
    {
        var paths = new FactoryPaths(Options.Create(new FactoryOptions { Home = _dir[home], EnvFile = _dir["none.env"] }), new GitClient(new ProcessRunner()));
        var state = Options.Create(new StateOptions { State = "sql", Sql = _sql, RenewSeconds = renewSeconds });
        var settings = new StateSettings(state, paths, new DotEnv(paths, new FileStore()));
        var leases = new StateLeases(settings, null!, new EfLeaseRows(new FactoryDbs(new StateDbConnection(settings)), TimeProvider.System));
        var file = Path.Combine(_dir[home], "runner.lock");
        return (new LeasedLock(file, "runner", leases, state), file);
    }

    private long? ExpiresAt()
    {
        using var db = new FactoryDbs(new TestDb(Data.Abstractions.DbProvider.SqlServer, _sql)).Open();
        return db.Leases.AsNoTracking().Where(l => l.Name == "runner").Select(l => (long?)l.ExpiresAt).FirstOrDefault();
    }

    [Fact]
    public void A_lease_held_by_another_machine_refuses_and_leaves_no_lock_file_until_it_is_released()
    {
        var (a, _) = Lock("a");
        var held = a.TryTake();
        Assert.NotNull(held);
        Assert.NotNull(ExpiresAt());

        var (b, file) = Lock("b");
        Assert.Null(b.TryTake(out var leaseRefused));
        Assert.True(leaseRefused);
        Assert.False(File.Exists(file));

        held!.Dispose();
        Assert.Null(ExpiresAt());
        using var next = b.TryTake();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task The_keeper_renews_the_lease_while_its_holder_works()
    {
        var (l, _) = Lock("a", renewSeconds: "1");
        using var held = l.TryTake();
        var first = ExpiresAt();

        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);

        Assert.True(ExpiresAt() > first);
    }
}
