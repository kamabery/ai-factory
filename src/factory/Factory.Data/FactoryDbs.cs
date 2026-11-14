using Factory.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Factory.Data;

/// <summary>Opens the provider's context and applies its pending migrations, once per process for each database.</summary>
public sealed class FactoryDbs(IDbConnectionInfo info)
{
    private static readonly HashSet<string> Migrated = [];

    public string TenantId => info.TenantId;

    public FactoryDb Open()
    {
        FactoryDb db = info.Provider == DbProvider.Sqlite
            ? new SqliteFactoryDb(new DbContextOptionsBuilder<SqliteFactoryDb>().UseSqlite(info.ConnectionString).Options)
            : new SqlServerFactoryDb(new DbContextOptionsBuilder<SqlServerFactoryDb>().UseSqlServer(info.ConnectionString).Options);
        var key = $"{info.Provider}|{info.ConnectionString}";
        lock (Migrated)
        {
            var file = info.Provider == DbProvider.Sqlite ? FileOf(info.ConnectionString) : null;
            // A SQLite file deleted since (simulate -Reset, under a running dashboard) starts again from its migrations.
            if (Migrated.Contains(key) && (file is null || File.Exists(file))) return db;
            if (file is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            // Migrate() diffs the model on every call; a database already at the latest migration needs only the query.
            if (db.Database.GetPendingMigrations().Any()) db.Database.Migrate();
            // WAL lets the dashboard's reads run beside a command's write; it stays set in the file.
            if (info.Provider == DbProvider.Sqlite) db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            Migrated.Add(key);
        }
        return db;
    }

    // An in-memory or URI data source has no folder to create.
    private static string? FileOf(string connectionString) =>
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource is { Length: > 0 } s && s != ":memory:"
            && !s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? s : null;
}
