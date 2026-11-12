using Factory.Core.Abstractions;
using Factory.Runner.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.Runner;

/// <summary>The runner's composition root: the only place that reads the environment.</summary>
public static class RunnerComposition
{
    public static IServiceCollection AddFactoryRunner(this IServiceCollection services, IReadOnlyDictionary<string, string?> env,
        string workingDirectory, string tempPath, string localAppData, string? configuredMode)
    {
        string? Get(string name) => env.GetValueOrDefault(name) is { Length: > 0 } v ? v : null;
        services.Configure<FactoryOptions>(o =>
        {
            o.Home = Get("FACTORY_HOME");
            o.Repo = Get("FACTORY_REPO");
            o.EnvFile = Get("FACTORY_ENV_FILE");
            o.Work = Get("FACTORY_WORK");
            o.Templates = Get("FACTORY_TEMPLATES");
            o.Scripts = Get("FACTORY_SCRIPTS");
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
            o.RenewSeconds = Get("FACTORY_STATE_RENEW_SECONDS");
        });
        services.Configure<RunnerOptions>(o =>
        {
            o.ClaudeCmd = Get("FACTORY_CLAUDE_CMD");
            o.ConfiguredMode = configuredMode;
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
        services.AddSingleton<IRunnerLock, RunnerLock>();
        services.AddSingleton<IFactoryCommands, FactoryScript>();
        services.AddSingleton<IClaudeRunner, ClaudeRunner>();
        services.AddSingleton<LlmProvider>();
        services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<LlmProvider>());
        services.AddSingleton<IRunnerLog, RunnerLog>();
        services.AddSingleton<RunnerLoop>();
        return services;
    }
}
