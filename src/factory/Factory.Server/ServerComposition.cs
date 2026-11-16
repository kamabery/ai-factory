using Factory.Core.Abstractions;
using Factory.Data;
using Factory.Data.Abstractions;
using Factory.Server.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.Server;

/// <summary>The dashboard server's composition root: the only place that reads the environment.</summary>
public static class ServerComposition
{
    /// <param name="env">The process environment.</param>
    /// <param name="stateDir">--state-dir, else FACTORY_HOME, else the main checkout's .factory-runs.</param>
    /// <param name="scripts">factory/scripts: FACTORY_SCRIPTS, else found above this server's binaries.</param>
    public static IServiceCollection AddFactoryServer(this IServiceCollection services, IReadOnlyDictionary<string, string?> env,
        string? stateDir, string scripts, string envFile, int port, int watchdogSeconds, int prSyncSeconds, int parentPid,
        string workingDirectory, string tempPath, string localAppData)
    {
        string? Get(string name) => env.GetValueOrDefault(name) is { Length: > 0 } v ? v : null;
        var work = Get("FACTORY_WORK") ?? Path.Combine(tempPath, "ai-factory");
        services.Configure<FactoryOptions>(o =>
        {
            o.Home = stateDir ?? Get("FACTORY_HOME");
            o.Repo = Get("FACTORY_REPO");
            o.EnvFile = envFile;
            o.Work = Get("FACTORY_WORK");
            o.Templates = Get("FACTORY_TEMPLATES");
            o.Scripts = scripts;
            o.Cli = Get("FACTORY_CLI");
            o.BaseDirectory = AppContext.BaseDirectory;
            o.Profile = Get("FACTORY_PROFILE");
            o.UserHome = Get(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") ?? "";
            o.DotnetCliHome = Get("DOTNET_CLI_HOME");
            o.TempPath = tempPath;
            o.WorkingDirectory = workingDirectory;
        });
        services.Configure<StateOptions>(o =>
        {
            o.State = Get("FACTORY_STATE");
            o.BlobUrl = Get("FACTORY_BLOB_URL");
            o.Cap = Get("FACTORY_STATE_CAP");
            o.Token = Get("FACTORY_STATE_TOKEN");
            o.LocalDir = Get("FACTORY_LOCAL_DIR") ?? Path.Combine(localAppData, "ai-factory");
            o.AzCmd = Get("FACTORY_AZ_CMD");
            o.IdentityEndpoint = Get("IDENTITY_ENDPOINT");
            o.IdentityHeader = Get("IDENTITY_HEADER");
            o.ClientId = Get("AZURE_CLIENT_ID");
            o.Db = Get("FACTORY_DB");
            o.Sql = Get("FACTORY_SQL");
        });
        services.Configure<ServerOptions>(o =>
        {
            o.Port = port;
            o.EnvFile = envFile;
            o.DashboardDir = Path.GetFullPath(Path.Combine(scripts, "..", "dashboard"));
            o.WatchdogSeconds = watchdogSeconds;
            o.PrSyncSeconds = prSyncSeconds;
            o.ParentPid = parentPid;
            o.StatePollSeconds = double.TryParse(Get("FACTORY_STATE_POLL_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out var poll) ? poll : 5;
            o.WorkRoot = work;
            o.Environment = env;
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFileStore, FileStore>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IFactoryPaths, FactoryPaths>();
        services.AddSingleton<IDotEnv, DotEnv>();
        services.AddSingleton<IStateSettings, StateSettings>();
        services.AddHttpClient<IStateToken, StateToken>();
        services.AddHttpClient<IBlobClient, BlobClient>(c => c.Timeout = TimeSpan.FromSeconds(100));
        services.AddSingleton<IStateSync, StateSync>();
        services.AddSingleton<IDbConnectionInfo, StateDbConnection>();
        services.AddSingleton<FactoryDbs>();
        services.AddSingleton<IBoardRows, EfBoardRows>();
        services.AddSingleton<IPause, ThreadPause>();
        services.AddSingleton<IStateExport, StateExport>();
        services.AddSingleton<IFactoryLauncher, FactoryLauncher>();

        services.AddSingleton<IFactoryCalls, FactoryCalls>();
        services.AddSingleton<IProcessProbe, ProcessProbe>();
        services.AddSingleton<IServerSettings, ServerSettings>();
        services.AddSingleton<IStateCache, StateCache>();
        services.AddSingleton<BoardFile>();
        services.AddSingleton<SessionFiles>();
        services.AddSingleton<StaticFiles>();
        services.AddSingleton<JobsApi>();
        services.AddSingleton<ReadsApi>();
        services.AddSingleton<IdeaSessions>();
        services.AddSingleton<Watchdog>();
        services.AddHostedService(sp => sp.GetRequiredService<Watchdog>());
        services.AddSingleton<ParentWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<ParentWatch>());
        services.AddHostedService<StartupSweep>();
        return services;
    }
}
