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
            o.StorageAccount = host.Get("FACTORY_STORAGE_ACCOUNT");
            o.StateGroup = host.Get("FACTORY_STATE_RG");
            o.IpUrl = host.Get("FACTORY_IP_URL");
            o.RenewSeconds = host.Get("FACTORY_STATE_RENEW_SECONDS");
        });
        services.Configure<DeployOptions>(o =>
        {
            o.GhCmd = host.Get("FACTORY_GH_CMD");
            o.AzCmd = host.Get("FACTORY_AZ_CMD");
            o.SigninCmd = host.Get("FACTORY_SIGNIN_CMD");
            o.CanaryScaffoldCmd = host.Get("FACTORY_CANARY_SCAFFOLD_CMD");
            o.PollSeconds = host.Get("FACTORY_DEPLOY_POLL_SECONDS");
            o.TimeoutMinutes = host.Get("FACTORY_DEPLOY_TIMEOUT_MINUTES");
            o.SmokeSeconds = host.Get("FACTORY_SMOKE_SECONDS");
            o.SigninSeconds = host.Get("FACTORY_SIGNIN_SECONDS");
            o.SigninRetrySeconds = host.Get("FACTORY_SIGNIN_RETRY_SECONDS");
            o.DestroyStaleMinutes = host.Get("FACTORY_DESTROY_STALE_MINUTES");
        });
        services.Configure<CliOptions>(o => o.NewLine = host.NewLine);
        services.Configure<LlmOptions>(o => o.Environment = host.Variables);
        services.AddSingleton(host);

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
        services.AddHttpClient<IStateFirewall, StateFirewall>(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IBoardMutex, BoardMutex>();
        services.AddSingleton<IFactoryStore, FactoryStore>();
        services.AddSingleton<ICanary, CanaryCheck>();
        services.AddSingleton<ITemplateProvenance, TemplateProvenance>();
        services.AddSingleton<IGitHubCredential, PatCredential>();
        services.AddHttpClient<IGitHub, GitHubClient>();
        services.AddSingleton<IGateShell, GateShell>();
        services.AddSingleton<IFactoryLauncher, FactoryLauncher>();
        services.AddSingleton<IJobWork, JobWork>();
        services.AddSingleton<IToolRunner, ToolRunner>();
        services.AddHttpClient<IHttpProbe, HttpProbe>();
        services.AddSingleton<IPause, ThreadPause>();
        services.AddSingleton<ICanaryLock, CanaryLock>();
        services.AddSingleton<IDeployService, DeployService>();
        services.AddSingleton<ICanaryService, CanaryService>();

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
        services.AddSingleton<ICommand, EvalCommand>();
        services.AddSingleton<ICommand, FeedbackCommand>();
        services.AddSingleton<ICommand, LessonCommand>();
        services.AddSingleton<ICommand, LessonMarkCommand>();
        services.AddSingleton<ICommand, StateMigrateCommand>();
        services.AddSingleton<ICommand, StatePullCommand>();
        services.AddSingleton<ICommand, PruneCommand>();
        services.AddSingleton<ICommand, AllowIpCommand>();
        services.AddSingleton<ICommand, DiscardCommand>();
        services.AddSingleton<ICommand, ResumeCommand>();
        services.AddSingleton<ICommand, DeployCommand>();
        services.AddSingleton<ICommand, DeployLogCommand>();
        services.AddSingleton<ICommand, DeployFixCommand>();
        services.AddSingleton<ICommand, BlockCommand>();
        services.AddSingleton<ICommand, RedeployCommand>();
        services.AddSingleton<ICommand, DestroyCommand>();
        services.AddSingleton<ICommand, SweepCommand>();
        services.AddSingleton<ICommand, CanaryCommand>();
        services.AddSingleton<ICommand, CanaryDestroyCommand>();
        services.AddSingleton<ICommand, QaLoginCommand>();
        services.AddSingleton<ILlmProvider, LlmProvider>();
        services.AddSingleton<IdeaFiles>();
        services.AddSingleton<IIdeaClaude, IdeaClaude>();
        services.AddSingleton<IStandaloneCommand, IdeaTurnCommand>();
        services.AddSingleton<IStandaloneCommand, IdeaMcpCommand>();
        services.AddSingleton<CliApp>();
        return services;
    }

    public static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}
