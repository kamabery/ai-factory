using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Factory.UnitTests;

public class CompositionTests
{
    private static CliEnvironment Host(Dictionary<string, string?>? env = null, string newLine = "\n") =>
        new(env ?? [], "w", newLine, "/appdata", "/tmp", "/nowhere");

    [Fact]
    public void The_container_builds_validated_and_resolves_every_service()
    {
        var services = new ServiceCollection().AddFactoryCli(Host());

        using var provider = Composition.Build(services);

        foreach (var d in services.Where(d => !d.ServiceType.IsGenericTypeDefinition))
            Assert.NotNull(provider.GetRequiredService(d.ServiceType));
        Assert.Equal(
            ["add-spec", "allow-ip", "approve", "block", "canary", "canary-destroy", "check-target", "checkout", "clone-target", "deploy", "deploy-fix", "deploy-log",
             "destroy", "discard", "edit-spec", "eval", "event", "feedback", "gate", "gate-check", "idea-abandon", "idea-done", "lesson", "lesson-mark", "list-repos",
             "merge", "new-idea", "next", "provenance", "prune", "publish", "purge", "qa-login", "read-decision", "read-reviews", "redeploy", "resume", "rework",
             "set-cost", "set-review", "set-stage", "state-migrate", "state-pull", "status", "sweep", "sync-prs"],
            provider.GetServices<ICommand>().Select(c => c.Name).Order());
    }

    [Fact]
    public void Binds_the_deploy_settings_from_the_environment()
    {
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_GH_CMD"] = "gh.ps1", ["FACTORY_AZ_CMD"] = "az.ps1", ["FACTORY_SIGNIN_CMD"] = "signin.ps1", ["FACTORY_CANARY_SCAFFOLD_CMD"] = "sc.ps1",
            ["FACTORY_DEPLOY_POLL_SECONDS"] = "1", ["FACTORY_DEPLOY_TIMEOUT_MINUTES"] = "2", ["FACTORY_SMOKE_SECONDS"] = "3", ["FACTORY_SIGNIN_SECONDS"] = "4",
            ["FACTORY_SIGNIN_RETRY_SECONDS"] = "5", ["FACTORY_DESTROY_STALE_MINUTES"] = "6",
        };

        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(Host(env)));

        var o = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Factory.Store.DeployOptions>>().Value;
        Assert.Equal(("gh.ps1", "az.ps1", "signin.ps1", "sc.ps1", 1, 2, 3, 4, 5, 6),
            (o.GhCmd, o.AzCmd, o.SigninCmd, o.CanaryScaffoldCmd, o.Poll, o.Timeout, o.Smoke, o.Signin, o.SigninRetry, o.DestroyStale));
    }

    [Fact]
    public void Binds_the_factory_settings_from_the_environment()
    {
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = "h", ["FACTORY_REPO"] = "r", ["FACTORY_ENV_FILE"] = "e", ["FACTORY_WORK"] = "wk",
            ["FACTORY_TEMPLATES"] = "t", ["FACTORY_SCRIPTS"] = "s", ["FACTORY_GATE_CMDS"] = "g", ["FACTORY_GATE_CHECK_WAIT_MINUTES"] = "5",
            ["FACTORY_MODE"] = "cloud", ["FACTORY_GATE_SHELL"] = "gs", ["FACTORY_CLI"] = "c", ["FACTORY_RUNNER"] = "rn", ["FACTORY_SERVER"] = "sv",
        };

        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(Host(env, "\r\n")));

        var o = provider.GetRequiredService<IOptions<FactoryOptions>>().Value;
        Assert.Equal(("h", "r", "w", "e", "wk", "t", "s", "/tmp"), (o.Home, o.Repo, o.WorkingDirectory, o.EnvFile, o.Work, o.Templates, o.Scripts, o.TempPath));
        Assert.Equal(("g", "5", "cloud", "gs"), (o.GateCmds, o.GateCheckWaitMinutes, o.Mode, o.GateShell));
        Assert.Equal(("c", "rn", "sv", "/nowhere"), (o.Cli, o.Runner, o.Server, o.BaseDirectory));
        Assert.Equal("\r\n", provider.GetRequiredService<IOptions<CliOptions>>().Value.NewLine);
    }

    [Fact]
    public void Binds_the_state_settings_from_the_environment()
    {
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_STATE"] = "azure", ["FACTORY_BLOB_URL"] = "u", ["FACTORY_STATE_CAP"] = "9", ["FACTORY_STATE_TOKEN"] = "t",
            ["FACTORY_LOCAL_DIR"] = "l", ["FACTORY_BOARD_LOCK_SECONDS"] = "1", ["FACTORY_STATE_LOCK_SECONDS"] = "2",
            ["FACTORY_AZ_CMD"] = "a", ["IDENTITY_ENDPOINT"] = "ie", ["IDENTITY_HEADER"] = "ih",
        };

        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(Host(env)));

        var o = provider.GetRequiredService<IOptions<StateOptions>>().Value;
        Assert.Equal(("azure", "u", "9", "t", "l", "1", "2", "a", "ie", "ih"),
            (o.State, o.BlobUrl, o.Cap, o.Token, o.LocalDir, o.BoardLockSeconds, o.StateLockSeconds, o.AzCmd, o.IdentityEndpoint, o.IdentityHeader));
    }

    [Fact]
    public void The_token_cache_defaults_to_the_app_data_folder_and_empty_settings_count_as_unset()
    {
        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(Host(new() { ["FACTORY_LOCAL_DIR"] = "", ["FACTORY_HOME"] = "" })));

        Assert.Equal(Path.Combine("/appdata", "ai-factory"), provider.GetRequiredService<IOptions<StateOptions>>().Value.LocalDir);
        Assert.Null(provider.GetRequiredService<IOptions<FactoryOptions>>().Value.Home);
    }
}
