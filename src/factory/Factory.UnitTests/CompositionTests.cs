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
            ["add-spec", "approve", "check-target", "clone-target", "edit-spec", "event", "idea-abandon", "idea-done", "list-repos", "new-idea", "purge", "read-decision", "read-reviews", "rework", "set-review", "set-stage", "status"],
            provider.GetServices<ICommand>().Select(c => c.Name).Order());
    }

    [Fact]
    public void Binds_the_factory_settings_from_the_environment()
    {
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = "h", ["FACTORY_REPO"] = "r", ["FACTORY_ENV_FILE"] = "e", ["FACTORY_WORK"] = "wk",
            ["FACTORY_TEMPLATES"] = "t", ["FACTORY_SCRIPTS"] = "s",
        };

        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(Host(env, "\r\n")));

        var o = provider.GetRequiredService<IOptions<FactoryOptions>>().Value;
        Assert.Equal(("h", "r", "w", "e", "wk", "t", "s", "/tmp"), (o.Home, o.Repo, o.WorkingDirectory, o.EnvFile, o.Work, o.Templates, o.Scripts, o.TempPath));
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
