using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Server.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Server;

/// <summary>
/// The contract's read-only dashboard endpoints (lessons, evaluations, idea config, sessions, repos) and the one-call
/// writes beside them (lesson-mark, feedback, purge, idea-abandon).
/// </summary>
public sealed partial class ReadsApi(IFactoryCalls factory, IFactoryPaths paths, IFileStore files, IServerSettings settings, IProcessProbe procs)
{
    /// <summary>Every lessons/&lt;id&gt;.json, newest job first.</summary>
    public Reply Lessons() => Reply.Data(NewestFirst(Path.Combine(paths.Home, "lessons")));

    public Reply MarkLesson(string id, string body)
    {
        var req = JsonNode.Parse(body) as JsonObject;
        var status = BoardFile.Str(req, "status") ?? "";
        var session = BoardFile.Str(req, "session");
        string[] args = string.IsNullOrEmpty(session) ? ["lesson-mark", id, status] : ["lesson-mark", id, status, "-Detail", session];
        return Reply.Of(factory.Run(args));
    }

    /// <summary>Every evals/&lt;id&gt;.json, newest job first.</summary>
    public Reply Evals() => Reply.Data(NewestFirst(Path.Combine(paths.Home, "evals")));

    public Reply Feedback(string id, string markdown) =>
        ServerFiles.WithTempFile(paths.Home, $"feedback-{procs.CurrentPid}.md", markdown, tmp => factory.Run("feedback", id, "-File", tmp), "feedback saved");

    /// <summary>What the header and New idea need; no key is ever returned.</summary>
    public Reply IdeateConfig()
    {
        var mode = settings.Mode;
        return Reply.Data(new JsonObject
        {
            ["enabled"] = settings.IdeaBlocker is null,
            ["mode"] = mode,
            ["provider"] = mode is null ? null : RunMode.Provider(mode),
            ["model"] = settings.IdeaModel,
            ["owner"] = settings.Env("GITHUB_OWNER"),
            ["templatesRepo"] = settings.Env("FACTORY_TEMPLATES_REPO"),
            ["deploys"] = settings.Env("HCP_ORG") is not null,
        });
    }

    /// <summary>list-repos' JSON array, for the New idea picker.</summary>
    public Reply Repos()
    {
        var r = factory.Run("list-repos");
        return r.Ok ? Reply.Raw(r.Message) : Reply.Of(r);
    }

    /// <summary>Every session that is not closed.</summary>
    public Reply Sessions()
    {
        var root = Path.Combine(paths.Home, "sessions");
        var list = new JsonArray();
        var found = files.Files(root).Where(f => Path.GetFileName(f.Path) == "session.json" && Parent(Parent(f.Path)) == Path.GetFullPath(root));
        foreach (var file in found.OrderBy(f => Path.GetFileName(Parent(f.Path)), StringComparer.InvariantCultureIgnoreCase))
        {
            if (files.ReadText(file.Path) is not { } text || JsonNode.Parse(text) is not JsonObject s) continue;
            if (BoardFile.Str(s, "status") == "closed") continue;
            var item = new JsonObject();
            foreach (var k in (string[])["id", "target", "text", "status", "idea", "bootstrap", "costUsd", "newTarget"])
                item[k] = s[k]?.DeepClone();
            list.Add(item);
        }
        return Reply.Data(list);
    }

    public Reply Purge() => Reply.Of(factory.Run("purge"));

    public Reply AbandonIdea(string id) => Reply.Of(factory.Run("idea-abandon", id));

    private JsonArray NewestFirst(string dir)
    {
        var items = files.Files(dir)
            .Where(f => Parent(f.Path) == Path.GetFullPath(dir))
            .Select(f => (f.Path, M: JobFile().Match(Path.GetFileName(f.Path))))
            .Where(x => x.M.Success)
            .OrderByDescending(x => long.Parse(Digits().Replace(Path.GetFileName(x.Path), ""), System.Globalization.CultureInfo.InvariantCulture))
            .Select(x => JsonNode.Parse(files.ReadText(x.Path) ?? "null"));
        return [.. items];
    }

    // -match, as serve.ps1 used it, ignores case.
    [GeneratedRegex(@"^[A-Z]+-(\d+)\.json$", RegexOptions.IgnoreCase)] private static partial Regex JobFile();
    [GeneratedRegex(@"\D")] private static partial Regex Digits();

    private static string Parent(string path) => Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
}
