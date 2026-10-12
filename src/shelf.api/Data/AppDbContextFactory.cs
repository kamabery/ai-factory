using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using shelf.data;

namespace shelf.api.Data;

/// <summary>
/// Builds a <see cref="AppDbContext"/> for the `dotnet ef` tooling, which cannot use the
/// application's own DI graph.
/// </summary>
/// <remarks>
/// <para>At design time there is no AppHost, so no connection string is injected and
/// AddNpgsqlDbContext / AddSqlServerDbContext would throw before the tooling got anywhere. The
/// connection strings below are placeholders: generating a migration needs the provider's SQL
/// generator and the model, never a reachable server.</para>
/// <para>It lives in shelf.api because that is the --startup-project the tooling is pointed at,
/// and EF looks for a design-time factory in the startup and target assemblies only.</para>
/// <para>The provider comes from the argument forwarded after `--`:
/// <c>dotnet ef migrations add Init --project ... -- --provider sqlserver</c>, falling back to the
/// same Database__Provider variable the running app reads so the two cannot disagree.</para>
/// </remarks>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var provider = ReadProvider(args);
        var options = new DbContextOptionsBuilder<AppDbContext>();

        if (DatabaseProvider.IsSqlite(provider))
        {
            options.UseSqlite(
                "Data Source=app.db",
                sqlite => sqlite.MigrationsAssembly(DatabaseProvider.SqliteMigrationsAssembly));
        }



        return new AppDbContext(options.Options);
    }

    private static string ReadProvider(string[] args)
    {
        var flag = Array.IndexOf(args, "--provider");

        if (flag >= 0 && flag + 1 < args.Length)
        {
            return args[flag + 1];
        }

        return Environment.GetEnvironmentVariable("Database__Provider") ?? DatabaseProvider.Default;
    }
}
