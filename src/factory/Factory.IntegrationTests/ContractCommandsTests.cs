using System.Text.RegularExpressions;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.IntegrationTests;

/// <summary>
/// The CLI against the contract's command table ("factory commands"): every command the table names is one the CLI has,
/// and the CLI has no board command the table does not name (no set-provider, say). Its standalone commands are each
/// named in the contract as <c>factory &lt;name&gt;</c>.
/// </summary>
public sealed partial class ContractCommandsTests
{
    private static readonly string Contract = File.ReadAllText(GitHubStub.RepoFile("docs", "FACTORY-CONTRACT.md"));

    private static ServiceProvider Cli() =>
        Composition.Build(new ServiceCollection().AddFactoryCli(new CliEnvironment(new Dictionary<string, string?>(), "w", "\n", "/appdata", "/tmp", "/nowhere")));

    [Fact]
    public void The_CLI_has_exactly_the_contracts_commands()
    {
        var section = Contract[Contract.IndexOf("## `factory` commands", StringComparison.Ordinal)..Contract.IndexOf("## Agents", StringComparison.Ordinal)];
        var table = section.Split('\n').Where(l => l.StartsWith("| `", StringComparison.Ordinal))
            .SelectMany(l => Command().Matches(l.Split('|')[1]).Select(m => m.Groups[1].Value)).Distinct().Order().ToList();
        using var provider = Cli();

        Assert.Equal(table, provider.GetServices<ICommand>().Select(c => c.Name).Order().ToList());
        Assert.DoesNotContain("set-provider", table);
    }

    [Fact]
    public void Each_standalone_command_is_in_the_contract()
    {
        using var provider = Cli();

        var missing = provider.GetServices<IStandaloneCommand>().Select(c => c.Name).Distinct()
            .Where(n => !Contract.Contains($"`factory {n}", StringComparison.Ordinal) && !Contract.Contains($"**`factory {n}", StringComparison.Ordinal)).ToList();

        Assert.Empty(missing);
    }

    // A command starts a cell of the table's first column, or follows a ·.
    [GeneratedRegex(@"(?:^|·)\s*`([a-z][a-z-]*)")] private static partial Regex Command();
}
