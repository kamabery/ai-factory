using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>new-idea &lt;text&gt; -Target &lt;owner/name&gt; [-Scaffold &lt;json&gt;] [-Base &lt;J-NNN&gt;]</c>: an idea on an existing
/// repo drafts; one on a new repo waits on a bootstrap job built from its scaffold options.
/// </summary>
public sealed class NewIdeaCommand(IFactoryPaths paths, IFileStore files, IGitHub github, TimeProvider clock) : ICommand
{
    public string Name => "new-idea";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var text = args.Arg(1);
        if (text.Length == 0) throw new FactoryException("usage: new-idea <text> -Target <owner/name> [-Scaffold <json>] [-Base <J-NNN>]");
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

        var status = isNew ? "waiting" : "drafting";
        var ideas = session.Board["ideas"] as JsonArray ?? [];
        var id = Ids.Next(session.Board, "I", ideas);
        var dir = Path.Combine(paths.Home, "ideas", id);
        files.EnsureDirectory(dir);
        List<string> md = [text, "", $"Target: {target}"];
        if (@base.Length > 0) md.Add($"Base: factory/{@base} (unmerged; this idea builds on it)");
        files.WriteAtomic(Path.Combine(dir, "idea.md"), new UTF8Encoding(false).GetBytes(PsText.Content(md, Environment.NewLine)));
        var now = clock.GetUtcNow();
        var node = new JsonObject
        {
            ["id"] = id, ["text"] = text, ["target"] = target, ["scaffold"] = sc, ["base"] = @base.Length > 0 ? @base : null,
            ["status"] = status, ["jobs"] = new JsonArray(), ["createdAt"] = Iso.Format(now),
        };
        session.Board["ideas"] = ideas;
        ideas.Add(node);
        session.Save();
        session.AddEvent(Events.Create(now, ("idea", id), ("kind", "idea"), ("detail", status)));
        if (sc is null) return CommandResult.Ok(id);

        var template = files.ReadText(Path.Combine(Path.GetDirectoryName(paths.ScriptsDir)!, "templates", "bootstrap.md")) ?? "";
        var lines = BootstrapSpec.Lines(sc, target, template, paths.TemplatesRoot);
        return CommandResult.Ok($"{id} {Specs.Add(paths, files, session, new Idea(node), lines, "the bootstrap template", "bootstrap", clock)}");
    }
}
