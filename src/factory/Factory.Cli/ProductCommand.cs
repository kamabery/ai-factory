using System.Text;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>product &lt;I-NNN&gt;</c>: a repo idea's features, read from <c>docs/PRODUCT.md</c> on its target's <c>main</c>, each with the
/// idea that started it, and the next to start (contract, "Ideas", "A New repo idea"). It changes nothing.
/// </summary>
public sealed class ProductCommand(IGitHub github) : ICommand
{
    public string Name => "product";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var idea = session.Board.Idea(args.Arg(1));
        if (!PsJsonReader.Is(PsJsonReader.Str(idea.Node["kind"]), "repo")) throw new FactoryException($"product refused: {idea.Id} is not a repo idea");
        var res = github.Send(HttpMethod.Get, $"/repos/{idea.Target}/contents/docs/PRODUCT.md?ref=main");
        if (res.Status != 200 || res.Body is not JsonObject body) throw new FactoryException($"product refused: docs/PRODUCT.md on main: {GitHubErrors.Text(res)}");
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(PsJsonReader.Text(body["content"]).Replace("\n", "", StringComparison.Ordinal)));
        var product = ProductText.Parse(text);
        if (product.Features.Count == 0) throw new FactoryException("product refused: docs/PRODUCT.md on main has no feature to start");

        var started = (idea.Node["features"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var list = new JsonArray();
        int? next = null;
        for (var k = 1; k <= product.Features.Count; k++)
        {
            var f = product.Features[k - 1];
            var by = started.FirstOrDefault(s => ProductText.SameHeading(PsJsonReader.Str(s["heading"]), f.Heading)) is { } s ? PsJsonReader.Str(s["idea"]) : null;
            if (by is null) next ??= k;
            list.Add(new JsonObject { ["k"] = k, ["heading"] = f.Heading, ["text"] = f.Text, ["idea"] = by });
        }
        return CommandResult.Ok(new JsonObject { ["features"] = list, ["next"] = next, ["home"] = product.Home }.ToJsonString(IdeaFiles.Compact));
    }
}
