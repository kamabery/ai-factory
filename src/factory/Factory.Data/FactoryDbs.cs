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
            if (Migrated.Contains(key)) return db;
            if (info.Provider == DbProvider.Sqlite && FileOf(info.ConnectionString) is { } file) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            db.Database.Migrate();
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
