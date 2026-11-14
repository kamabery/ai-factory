using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Factory.Data;

/// <summary>For <c>dotnet ef migrations add &lt;name&gt; --context SqliteFactoryDb -o Migrations/Sqlite</c> only.</summary>
public sealed class SqliteDesignFactory : IDesignTimeDbContextFactory<SqliteFactoryDb>
{
    public SqliteFactoryDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SqliteFactoryDb>().UseSqlite("Data Source=design.db").Options);
}
