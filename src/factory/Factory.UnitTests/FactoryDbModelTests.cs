using Factory.Data;
using Microsoft.EntityFrameworkCore;

namespace Factory.UnitTests;

/// <summary>The EF Core model on both providers, built without a connection.</summary>
public class FactoryDbModelTests
{
    // The design-time factories dotnet ef uses: they open nothing until a query runs.
    private static IEnumerable<FactoryDb> Both() => [new SqliteDesignFactory().CreateDbContext([]), new SqlServerDesignFactory().CreateDbContext([])];

    [Fact]
    public void Every_table_has_TenantId_and_every_key_but_Events_starts_with_it()
    {
        foreach (var db in Both())
        {
            using (db)
            {
                var tables = db.Model.GetEntityTypes().ToList();
                Assert.Equal(["Boards", "Events", "Ideas", "Jobs", "Leases"], tables.Select(t => t.GetTableName()).Order());
                Assert.All(tables, t => Assert.NotNull(t.FindProperty("TenantId")));
                Assert.All(tables.Where(t => t.GetTableName() != "Events"), t => Assert.Equal("TenantId", t.FindPrimaryKey()!.Properties[0].Name));
            }
        }
    }

    [Fact]
    public void Each_provider_has_its_own_migrations_and_none_is_missing_from_the_model()
    {
        foreach (var db in Both())
        {
            using (db)
            {
                Assert.NotEmpty(db.Database.GetMigrations());
                Assert.False(db.Database.HasPendingModelChanges(), $"{db.GetType().Name}: run dotnet ef migrations add");
            }
        }
    }

    [Fact]
    public void The_row_versions_are_concurrency_tokens()
    {
        using var db = Both().First();
        foreach (var name in new[] { "Boards", "Ideas", "Jobs" })
            Assert.True(db.Model.GetEntityTypes().Single(t => t.GetTableName() == name).FindProperty("Version")!.IsConcurrencyToken);
    }
}
