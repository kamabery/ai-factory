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
        var services = new ServiceCollection().AddFactoryCli(new Dictionary<string, string?>(), "/", "\n");

        using var provider = Composition.Build(services);

        foreach (var d in services.Where(d => !d.ServiceType.IsGenericTypeDefinition))
            Assert.NotNull(provider.GetRequiredService(d.ServiceType));
        Assert.Contains(provider.GetServices<ICommand>(), c => c.Name == "status");
    }

    [Fact]
    public void Binds_the_factory_settings_from_the_environment()
    {
        var env = new Dictionary<string, string?> { ["FACTORY_HOME"] = "h", ["FACTORY_REPO"] = "r" };

        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(env, "w", "\r\n"));

        var o = provider.GetRequiredService<IOptions<FactoryOptions>>().Value;
        Assert.Equal(("h", "r", "w"), (o.Home, o.Repo, o.WorkingDirectory));
        Assert.Equal("\r\n", provider.GetRequiredService<IOptions<CliOptions>>().Value.NewLine);
    }
}
