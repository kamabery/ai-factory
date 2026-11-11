using Factory.Cli.Abstractions;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.Cli;

/// <summary>The CLI's composition root: the only place that reads the environment.</summary>
public static class Composition
{
    public static IServiceCollection AddFactoryCli(this IServiceCollection services, CliEnvironment host)
    {
        services.Configure<FactoryOptions>(o =>
        {
            o.Home = host.Get("FACTORY_HOME");
            o.Repo = host.Get("FACTORY_REPO");
            o.EnvFile = host.Get("FACTORY_ENV_FILE");
            o.Work = host.Get("FACTORY_WORK");
            o.Templates = host.Get("FACTORY_TEMPLATES");
            o.Scripts = host.Get("FACTORY_SCRIPTS") ?? host.FindScripts();
            o.GateCmds = host.Get("FACTORY_GATE_CMDS");
            o.GateCheckWaitMinutes = host.Get("FACTORY_GATE_CHECK_WAIT_MINUTES");
            o.Mode = host.Get("FACTORY_MODE");
            o.TempPath = host.TempPath;
            o.WorkingDirectory = host.WorkingDirectory;
        });
        services.Configure<StateOptions>(o =>
        {
            o.State = host.Get("FACTORY_STATE");
            o.BlobUrl = host.Get("FACTORY_BLOB_URL");
            o.Cap = host.Get("FACTORY_STATE_CAP");
            o.Token = host.Get("FACTORY_STATE_TOKEN");
            o.LocalDir = host.Get("FACTORY_LOCAL_DIR") ?? Path.Combine(host.LocalAppData, "ai-factory");
            o.BoardLockSeconds = host.Get("FACTORY_BOARD_LOCK_SECONDS");
            o.StateLockSeconds = host.Get("FACTORY_STATE_LOCK_SECONDS");
            o.AzCmd = host.Get("FACTORY_AZ_CMD");
            o.IdentityEndpoint = host.Get("IDENTITY_ENDPOINT");
            o.IdentityHeader = host.Get("IDENTITY_HEADER");
        });
        services.Configure<CliOptions>(o => o.NewLine = host.NewLine);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFileStore, FileStore>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IFactoryPaths, FactoryPaths>();
        services.AddSingleton<IDotEnv, DotEnv>();
        services.AddSingleton<IStateSettings, StateSettings>();
        services.AddHttpClient<IStateToken, StateToken>();
        services.AddHttpClient<IBlobClient, BlobClient>(c => c.Timeout = TimeSpan.FromSeconds(100));
        services.AddHttpClient<IHcpClient, HcpClient>();
        services.AddSingleton<IStateSync, StateSync>();
        services.AddSingleton<IBoardMutex, BoardMutex>();
        services.AddSingleton<IFactoryStore, FactoryStore>();
        services.AddSingleton<ICanary, CanaryCheck>();
        services.AddSingleton<ITemplateProvenance, TemplateProvenance>();
        services.AddSingleton<IGitHubCredential, PatCredential>();
        services.AddHttpClient<IGitHub, GitHubClient>();
        services.AddSingleton<IGateShell, GateShell>();
        services.AddSingleton<IFactoryLauncher, FactoryLauncher>();
        services.AddSingleton<IJobWork, JobWork>();

        services.AddSingleton<ICommand, StatusCommand>();
        services.AddSingleton<ICommand, ApproveCommand>();
        services.AddSingleton<ICommand, EditSpecCommand>();
        services.AddSingleton<ICommand, ReworkCommand>();
        services.AddSingleton<ICommand, PurgeCommand>();
        services.AddSingleton<ICommand, SetStageCommand>();
        services.AddSingleton<ICommand, SetReviewCommand>();
        services.AddSingleton<ICommand, ReadReviewsCommand>();
        services.AddSingleton<ICommand, ReadDecisionCommand>();
        services.AddSingleton<ICommand, EventCommand>();
        services.AddSingleton<ICommand, NewIdeaCommand>();
        services.AddSingleton<ICommand, AddSpecCommand>();
        services.AddSingleton<ICommand, IdeaDoneCommand>();
        services.AddSingleton<ICommand, IdeaAbandonCommand>();
        services.AddSingleton<ICommand, CheckTargetCommand>();
        services.AddSingleton<ICommand, ListReposCommand>();
        services.AddSingleton<ICommand, CloneTargetCommand>();
        services.AddSingleton<ICommand, CheckoutCommand>();
        services.AddSingleton<ICommand, PublishCommand>();
        services.AddSingleton<ICommand, GateJobCommand>();
        services.AddSingleton<ICommand, GateCheckCommand>();
        services.AddSingleton<ICommand, ProvenanceCommand>();
        services.AddSingleton<ICommand, MergeCommand>();
        services.AddSingleton<ICommand, SyncPrsCommand>();
        services.AddSingleton<ICommand, SetCostCommand>();
        services.AddSingleton<ICommand, NextCommand>();
        services.AddSingleton<CliApp>();
        return services;
    }

    public static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}
