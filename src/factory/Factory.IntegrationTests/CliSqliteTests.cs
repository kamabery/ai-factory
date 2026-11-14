namespace Factory.IntegrationTests;

/// <summary><see cref="CliDatabaseContract"/> on sqlite, plus what only a file database does.</summary>
public sealed class CliSqliteTests : CliDatabaseContract
{
    protected override Dictionary<string, string?> Backend() => new() { ["FACTORY_STATE"] = "sqlite" };

    [Fact]
    public void The_database_is_factory_db_in_the_home_in_WAL_mode()
    {
        Run("status");
        Run("event", "-Kind", "note", "-Detail", "x");

        Assert.True(File.Exists(Path.Combine(Home, "factory.db")));
        Factory.Data.SqliteNative.Init();
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(Home, "factory.db")};Pooling=False");
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", q.ExecuteScalar());
    }

    [Fact]
    public void FACTORY_DB_names_the_file_and_its_folder_is_created()
    {
        var db = Path.Combine(Dir["elsewhere"], "nested", "f.db");

        Assert.Equal(0, RunWith(("FACTORY_DB", db), "event", "-Kind", "note", "-Detail", "x").Code);

        Assert.True(File.Exists(db));
        Assert.False(File.Exists(Path.Combine(Home, "factory.db")));
    }

    [Fact]
    public void A_database_it_cannot_open_is_refused_in_one_line()
    {
        var (code, _, err) = RunWith(("FACTORY_DB", Dir.Path), "status");

        Assert.Equal(1, code);
        // The wording is SQLite's: e_sqlite3 says error 14, the OS's libsqlite3 (Ubuntu 20.04) error 10, disk I/O.
        Assert.StartsWith("factory: state: the database refused: SQLite Error ", err, StringComparison.Ordinal);
        Assert.Single(err.TrimEnd('\n').Split('\n'));
    }

    private (int Code, string Out, string Err) RunWith((string Name, string Value) setting, params string[] args)
    {
        var env = new Dictionary<string, string?>(Backend()) { ["FACTORY_HOME"] = Home, ["FACTORY_ENV_FILE"] = Dir["none.env"], [setting.Name] = setting.Value };
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = Factory.Cli.CliHost.Run(args, env, Dir.Path, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
