using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Factory.Data;

/// <summary>For <c>dotnet ef migrations add &lt;name&gt; --context SqlServerFactoryDb -o Migrations/SqlServer</c> only.</summary>
public sealed class SqlServerDesignFactory : IDesignTimeDbContextFactory<SqlServerFactoryDb>
{
    public SqlServerFactoryDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SqlServerFactoryDb>().UseSqlServer("Server=design").Options);
}
