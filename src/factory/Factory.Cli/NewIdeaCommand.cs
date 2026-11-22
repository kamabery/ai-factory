using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>new-idea &lt;text&gt; -Target &lt;owner/name&gt; [-Scaffold &lt;json&gt; [-Product &lt;file&gt;]] [-Base &lt;J-NNN&gt;] [-From &lt;I-NNN&gt; -Feature &lt;heading&gt;]</c>:
/// an idea on an existing repo drafts; one on a new repo waits on a bootstrap job built from its scaffold options, and
/// with a product it is a New repo idea; one from a repo idea starts that feature (contract, "Ideas").
/// </summary>
public sealed class NewIdeaCommand(IFactoryPaths paths, IFileStore files, IGitHub github, TimeProvider clock) : ICommand
{
    public string Name => "new-idea";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var text = args.Arg(1);
        if (text.Length == 0) throw new FactoryException("usage: new-idea <text> -Target <owner/name> [-Scaffold <json> [-Product <file>]] [-Base <J-NNN>] [-From <I-NNN> -Feature <heading>]");
        var target = args.Named("Target");
        var @base = args.Named("Base");
        if (SpecText.TargetError(target) is { } err) throw new FactoryException($"new-idea refused: {err}");
        var isNew = Targets.IsNew(github, target, "new-idea");
        if (@base.Length > 0)
        {
            if (isNew) throw new FactoryException($"base refused: '{target}' is a new repo");
            if (MergeRules.BaseError(session.Board, target, @base) is { } why) throw new FactoryException($"base refused: {why}");
        }
        var scaffold = args.Named("Scaffold");
        JsonObject? sc = null;
        if (isNew)
        {
            if (scaffold.Length == 0) throw new FactoryException($"new-idea refused: '{target}' is a new repo, so it needs scaffold options");
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(scaffold); } catch (JsonException) { throw new FactoryException("new-idea refused: the scaffold options are not valid JSON"); }
            if (Scaffold.Error(parsed) is { } why) throw new FactoryException($"new-idea refused: {why}");
            sc = (JsonObject)parsed!;
        }
        else if (scaffold.Length > 0) throw new FactoryException($"new-idea refused: '{target}' is an existing repo; scaffold options are only for a new one");
        var product = Product(args, target, isNew);
        var from = From(args, session, target);

        var status = isNew ? "waiting" : "drafting";
        var ideas = session.Board["ideas"] as JsonArray ?? [];
        var id = Ids.Next(session.Board, "I", ideas);
        var dir = Path.Combine(paths.Home, "ideas", id);
        files.EnsureDirectory(dir);
        List<string> md = [text, "", $"Target: {target}"];
        if (@base.Length > 0) md.Add($"Base: factory/{@base} (unmerged; this idea builds on it)");
        files.WriteAtomic(Path.Combine(dir, "idea.md"), new UTF8Encoding(false).GetBytes(PsText.Content(md, Environment.NewLine)));
        if (product is not null) files.WriteAtomic(Path.Combine(dir, "product.md"), new UTF8Encoding(false).GetBytes(product));
        var now = clock.GetUtcNow();
        var node = new JsonObject
        {
            ["id"] = id, ["text"] = text, ["target"] = target, ["scaffold"] = sc, ["base"] = @base.Length > 0 ? @base : null,
            ["status"] = status, ["jobs"] = new JsonArray(), ["createdAt"] = Iso.Format(now),
        };
        if (product is not null) { node["kind"] = "repo"; node["features"] = new JsonArray(); }
        if (from is var (repo, heading))
        {
            node["from"] = repo.Id;
            node["feature"] = heading;
            if (repo.Node["features"] is not JsonArray started) repo.Node["features"] = started = [];
            started.Add(new JsonObject { ["heading"] = heading, ["idea"] = id });
        }
        session.Board["ideas"] = ideas;
        ideas.Add(node);
        session.Save();
        session.AddEvent(Events.Create(now, ("idea", id), ("kind", "idea"), ("detail", status)));
        if (sc is null) return CommandResult.Ok(id);

        var template = files.ReadText(Path.Combine(Path.GetDirectoryName(paths.ScriptsDir)!, "templates", "bootstrap.md")) ?? "";
        var lines = BootstrapSpec.Lines(sc, target, template, paths.TemplatesRoot);
        return CommandResult.Ok($"{id} {Specs.Add(paths, files, session, new Idea(node), lines, "the bootstrap template", "bootstrap", clock)}");
    }

    /// <summary>-Product: a New repo idea's product document, read and checked (a new target only).</summary>
    private string? Product(CommandArgs args, string target, bool isNew)
    {
        var file = args.Named("Product");
        if (file.Length == 0) return null;
        if (!isNew) throw new FactoryException($"new-idea refused: '{target}' is an existing repo; a product is only for a new one");
        var text = files.ReadText(file) ?? throw new FactoryException("new-idea refused: no product file");
        if (ProductText.Error(text) is { } why) throw new FactoryException($"new-idea refused: {why["product refused: ".Length..]}");
        return text;
    }

    /// <summary>-From and -Feature: the drafting repo idea on this target, and a heading it has not started.</summary>
    private static (Idea Repo, string Heading)? From(CommandArgs args, IBoardSession session, string target)
    {
        var from = args.Named("From");
        var heading = args.Named("Feature").Trim();
        if (from.Length == 0 && heading.Length == 0) return null;
        if (from.Length == 0 || heading.Length == 0) throw new FactoryException("new-idea refused: -From and -Feature go together");
        var repo = session.Board.Ideas().FirstOrDefault(i => PsJsonReader.Is(i.Id, from)) ?? throw new FactoryException($"new-idea refused: no idea {from}");
        if (!PsJsonReader.Is(PsJsonReader.Str(repo.Node["kind"]), "repo")) throw new FactoryException($"new-idea refused: {repo.Id} is not a repo idea");
        if (!PsJsonReader.Is(repo.Status, "drafting")) throw new FactoryException($"new-idea refused: {repo.Id} is {repo.Status}, not a drafting repo idea");
        if (!PsJsonReader.Is(repo.Target, target)) throw new FactoryException($"new-idea refused: {repo.Id} is a repo idea on {repo.Target}, not {target}");
        if ((repo.Node["features"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(f => ProductText.SameHeading(PsJsonReader.Str(f["heading"]), heading)) is { } started)
            throw new FactoryException($"new-idea refused: feature '{PsJsonReader.Str(started["heading"])}' of {repo.Id} already has {PsJsonReader.Str(started["idea"])}");
        return (repo, heading);
    }
}
