using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>list-repos</c>: the credential's user's own repos as a compact JSON array, newest push first.</summary>
public sealed class ListReposCommand(IGitHub github) : ICommand
{
    public string Name => "list-repos";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        IReadOnlyList<string> repos;
        try { repos = github.OwnedRepos(); }
        catch (FactoryException e) { throw new FactoryException($"list-repos refused: {e.Message}"); }
        return CommandResult.Ok(PsJson.WriteCompact(new JsonArray([.. repos.Select(r => (JsonNode)r)])));
    }
}
