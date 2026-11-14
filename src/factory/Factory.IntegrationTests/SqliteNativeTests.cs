using Factory.Data;

namespace Factory.IntegrationTests;

/// <summary>
/// Which SQLite the sqlite backend runs on (contract, "State backends", Database): EF Core's e_sqlite3, or the OS's
/// libsqlite3 where that needs a newer glibc than the machine has (Ubuntu 20.04).
/// </summary>
public sealed class SqliteNativeTests
{
    [Fact]
    public void Loads_once_and_says_which_sqlite_it_runs_on()
    {
        SqliteNative.Init();
        SqliteNative.Init();

        Assert.Contains(SqliteNative.Library, new[] { "e_sqlite3", "sqlite3" });
        Assert.True(SQLitePCL.raw.sqlite3_libversion_number() >= 3_031_000, "SQLite 3.31 (Ubuntu 20.04's) or later");
        if (OperatingSystem.IsWindows()) Assert.Equal("e_sqlite3", SqliteNative.Library);
    }
}
