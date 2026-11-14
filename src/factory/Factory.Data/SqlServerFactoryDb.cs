using Microsoft.EntityFrameworkCore;

namespace Factory.Data;

/// <summary>The <c>sql</c> backend's context (Azure SQL, SQL Server); its migrations are in Migrations/SqlServer.</summary>
public sealed class SqlServerFactoryDb(DbContextOptions<SqlServerFactoryDb> options) : FactoryDb(options);
