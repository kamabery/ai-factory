using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Factory.UnitTests;

public class CompositionTests
{
    [Fact]
    public void The_container_builds_validated_and_resolves_every_service()
    {
        var services = new ServiceCollection().AddFactoryCli(new Dictionary<string, string?>(), "/", "\n", "/appdata");

        using var provider = Composition.Build(services);

        foreach (var d in services.Where(d => !d.ServiceType.IsGenericTypeDefinition))
            Assert.NotNull(provider.GetRequiredService(d.ServiceType));
        Assert.Contains(provider.GetServices<ICommand>(), c => c.Name == "status");
    }

    [Fact]
    public void Binds_the_factory_settings_from_the_environment()
    {
        var env = new Dictionary<string, string?> { ["FACTORY_HOME"] = "h", ["FACTORY_REPO"] = "r", ["FACTORY_ENV_FILE"] = "e" };

        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(env, "w", "\r\n", "/appdata"));

        var o = provider.GetRequiredService<IOptions<FactoryOptions>>().Value;
        Assert.Equal(("h", "r", "w", "e"), (o.Home, o.Repo, o.WorkingDirectory, o.EnvFile));
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

        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(env, "w", "\n", "/appdata"));

        var o = provider.GetRequiredService<IOptions<StateOptions>>().Value;
        Assert.Equal(("azure", "u", "9", "t", "l", "1", "2", "a", "ie", "ih"),
            (o.State, o.BlobUrl, o.Cap, o.Token, o.LocalDir, o.BoardLockSeconds, o.StateLockSeconds, o.AzCmd, o.IdentityEndpoint, o.IdentityHeader));
    }

    [Fact]
    public void The_token_cache_defaults_to_the_app_data_folder()
    {
        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(new Dictionary<string, string?>(), "w", "\n", "/appdata"));

        Assert.Equal(Path.Combine("/appdata", "ai-factory"), provider.GetRequiredService<IOptions<StateOptions>>().Value.LocalDir);
    }
}
