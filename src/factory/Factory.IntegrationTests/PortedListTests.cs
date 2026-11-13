using System.Text.RegularExpressions;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.IntegrationTests;

/// <summary>
/// factory.ps1's strangler list against the CLI (contract, "Implementations"): every command factory.ps1 has is forwarded
/// with FACTORY_IMPL=dotnet (spec 09 ported the last, the deploy set), and only to a command the CLI has.
/// </summary>
public sealed partial class PortedListTests
{
    [Fact]
    public void Factory_ps1_forwards_every_command_it_has_and_the_CLI_has_each_one()
    {
        var script = File.ReadAllText(GitHubStub.RepoFile("factory", "scripts", "factory.ps1"));
        var ported = Quoted().Matches(PortedList().Match(script).Groups[1].Value).Select(m => m.Groups[1].Value).Order().ToList();
        var cases = Case().Matches(script[script.IndexOf("switch ($Command)", StringComparison.Ordinal)..]).Select(m => m.Groups[1].Value).Order().ToList();
        var host = new CliEnvironment(new Dictionary<string, string?>(), "w", "\n", "/appdata", "/tmp", "/nowhere");
        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(host));
        var cli = provider.GetServices<ICommand>().Select(c => c.Name).Order().ToList();

        Assert.Equal(cases, ported);
        Assert.Equal(cli, ported);
    }

    [GeneratedRegex(@"\$Ported = @\(([^)]*)\)")] private static partial Regex PortedList();
    [GeneratedRegex(@"'([a-z-]+)'")] private static partial Regex Quoted();
    [GeneratedRegex(@"^        '([a-z-]+)' \{", RegexOptions.Multiline)] private static partial Regex Case();
}
