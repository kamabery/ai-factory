using Factory.Cli.Abstractions;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.Cli;

/// <summary>The CLI's composition root: the only place that reads the environment.</summary>
public static class Composition
{
    public static IServiceCollection AddFactoryCli(this IServiceCollection services,
        IReadOnlyDictionary<string, string?> env, string workingDirectory, string newLine, string localAppData)
    {
        string? Env(string name) => env.GetValueOrDefault(name);
        services.Configure<FactoryOptions>(o =>
        {
            o.Home = Env("FACTORY_HOME");
            o.Repo = Env("FACTORY_REPO");
            o.EnvFile = Env("FACTORY_ENV_FILE");
            o.WorkingDirectory = workingDirectory;
        });
        services.Configure<StateOptions>(o =>
        {
            o.State = Env("FACTORY_STATE");
            o.BlobUrl = Env("FACTORY_BLOB_URL");
            o.Cap = Env("FACTORY_STATE_CAP");
            o.Token = Env("FACTORY_STATE_TOKEN");
            o.LocalDir = Env("FACTORY_LOCAL_DIR") is { Length: > 0 } dir ? dir : Path.Combine(localAppData, "ai-factory");
            o.BoardLockSeconds = Env("FACTORY_BOARD_LOCK_SECONDS");
            o.StateLockSeconds = Env("FACTORY_STATE_LOCK_SECONDS");
            o.AzCmd = Env("FACTORY_AZ_CMD");
            o.IdentityEndpoint = Env("IDENTITY_ENDPOINT");
            o.IdentityHeader = Env("IDENTITY_HEADER");
        });
        services.Configure<CliOptions>(o => o.NewLine = newLine);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFileStore, FileStore>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IFactoryPaths, FactoryPaths>();
        services.AddSingleton<IStateSettings, StateSettings>();
        services.AddHttpClient<IStateToken, StateToken>();
        services.AddHttpClient<IBlobClient, BlobClient>(c => c.Timeout = TimeSpan.FromSeconds(100));
        services.AddSingleton<IStateSync, StateSync>();
        services.AddSingleton<IBoardMutex, BoardMutex>();
        services.AddSingleton<IFactoryStore, FactoryStore>();
        services.AddSingleton<ICommand, StatusCommand>();
        services.AddSingleton<CliApp>();
        return services;
    }

    public static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}
