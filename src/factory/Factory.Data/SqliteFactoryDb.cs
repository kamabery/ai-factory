using Microsoft.EntityFrameworkCore;

namespace Factory.Data;

/// <summary>The <c>sqlite</c> backend's context; its migrations are in Migrations/Sqlite.</summary>
public sealed class SqliteFactoryDb(DbContextOptions<SqliteFactoryDb> options) : FactoryDb(options);
