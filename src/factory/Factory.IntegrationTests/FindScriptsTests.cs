using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary>The factory's checkout, found above a build folder by its plugin: factory.ps1 is gone (spec 12).</summary>
public sealed class FindScriptsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_checkout_is_marked_by_the_factory_plugin_not_by_factory_ps1()
    {
        var plugin = Directory.CreateDirectory(_dir["repo/factory/plugin/.claude-plugin"]).FullName;
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), "{}");
        var bin = Directory.CreateDirectory(_dir["repo/artifacts/bin/Factory.Cli/debug"]).FullName;

        Assert.Equal(Path.GetFullPath(_dir["repo/factory/scripts"]), FactoryPaths.FindScripts(bin));
    }

    [Fact]
    public void Without_the_plugin_above_it_there_is_none()
    {
        Directory.CreateDirectory(_dir["repo/factory/scripts"]);
        File.WriteAllText(_dir["repo/factory/scripts/factory.ps1"], "");

        Assert.Null(FactoryPaths.FindScripts(Directory.CreateDirectory(_dir["repo/artifacts"]).FullName));
    }
}
