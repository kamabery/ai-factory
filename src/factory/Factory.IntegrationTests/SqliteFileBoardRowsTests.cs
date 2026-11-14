using Factory.Data.Abstractions;

namespace Factory.IntegrationTests;

/// <summary><see cref="BoardRowsContract"/> on a SQLite file, as the sqlite backend keeps it.</summary>
public sealed class SqliteFileBoardRowsTests : BoardRowsContract, IDisposable
{
    private readonly TempDir _dir = new();

    public SqliteFileBoardRowsTests() => Db = TestDb.SqliteFile(_dir.Path);

    protected override IDbConnectionInfo Db { get; }

    public void Dispose() => _dir.Dispose();
}
