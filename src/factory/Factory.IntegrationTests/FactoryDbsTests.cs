using Factory.Data;

namespace Factory.IntegrationTests;

/// <summary>Opening the database: migrations once per process, and again for a SQLite file that was deleted since (simulate -Reset).</summary>
public sealed class FactoryDbsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_sqlite_file_deleted_under_a_long_running_process_is_migrated_again()
    {
        var info = TestDb.SqliteFile(_dir.Path);
        var rows = new EfBoardRows(new FactoryDbs(info));
        rows.AddEvent("{}");

        File.Delete(Path.Combine(_dir.Path, "factory.db"));

        Assert.Equal(0, rows.Revision());
        rows.AddEvent("{}");
        Assert.Equal(1, rows.Revision());
    }
}
