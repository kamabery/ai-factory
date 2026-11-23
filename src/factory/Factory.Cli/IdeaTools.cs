using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Core;

namespace Factory.Cli;

/// <summary>One tool call's answer: the text the model reads, and whether it is an error.</summary>
public sealed record ToolAnswer(string Text, bool IsError = false);

/// <summary>
/// The idea session's tools (contract, "Idea sessions", Tools): read_file, list_dir and grep fenced to the target's clone
/// and factory/templates; submit_specs and revise_spec writing ideas/&lt;I&gt;/; and the UI tools, which record
/// <c>pending</c> and end the turn. One instance serves one turn: after a UI tool, every call is refused.
/// </summary>
public sealed partial class IdeaTools(IdeaFiles files, string session)
{
    public static readonly string[] UiTools = ["ask_questions", "propose_scaffold", "submit_brief", "submit_product"];
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs" };
    private const long MaxFile = 256 * 1024;
    private const int MaxHits = 200;

    /// <summary>What a valid UI call answers: the model is to stop.</summary>
    public const string Shown = "Shown to the human. Your turn is over: stop now, say nothing more and call no other tool. Their answer comes as the next message.";

    private string? _ui;

    /// <summary>The MCP tool list, with each tool's JSON schema (idea-turn.ps1's Get-Tools).</summary>
    public static JsonArray Definitions()
    {
        static JsonObject Obj(params (string K, JsonNode? V)[] p) { var o = new JsonObject(); foreach (var (k, v) in p) o[k] = v; return o; }
        static JsonObject Str(string? description = null) => description is null ? Obj(("type", "string")) : Obj(("type", "string"), ("description", description));
        static JsonObject Schema(JsonObject props, params string[] required) =>
            Obj(("type", "object"), ("properties", props), ("required", new JsonArray([.. required.Select(r => (JsonNode)r)])));
        static JsonObject Tool(string name, string description, JsonObject schema) => Obj(("name", name), ("description", description), ("inputSchema", schema));

        var spec = Schema(Obj(("k", Obj(("type", "integer"))), ("markdown", Str())), "k", "markdown");
        var option = Schema(Obj(("label", Str()), ("description", Str())), "label", "description");
        var question = Schema(Obj(
            ("question", Str()), ("header", Str("A chip label, at most 12 characters")), ("multiSelect", Obj(("type", "boolean"))),
            ("options", Obj(("type", "array"), ("items", option), ("description", "2-4 options; the panel adds Other")))),
            "question", "header", "options", "multiSelect");
        return
        [
            Tool("read_file", "Read a text file in the target repo (or factory/templates). A relative path is taken from the target root.", Schema(Obj(("path", Str())), "path")),
            Tool("list_dir", "List one folder of the target repo (default: its root). Folders end in /.", Schema(Obj(("path", Str())))),
            Tool("grep", "Search file contents under the target repo with a .NET regex. Prints path:line: text, at most 200 lines.", Schema(Obj(
                ("pattern", Str()), ("path", Str("folder to search, default the target root")), ("glob", Str("file name filter such as *.cs"))), "pattern")),
            Tool("submit_specs", "Write the specs for the confirmed brief: 1-5 of them, k = 1..N, each the full markdown starting with \"# Spec: <title>\". Only after the brief is confirmed.",
                Schema(Obj(("specs", Obj(("type", "array"), ("items", spec)))), "specs")),
            Tool("revise_spec", "Replace spec k (not yet decided by the human) with new full markdown.", Schema(Obj(("k", Obj(("type", "integer"))), ("markdown", Str())), "k", "markdown")),
            Tool("ask_questions", "Ask the human 1-4 questions in one round, each with 2-4 options. Ends your turn; the answers come back as the next message.",
                Schema(Obj(("questions", Obj(("type", "array"), ("items", question)))), "questions")),
            Tool("propose_scaffold", "New target only: propose its scaffold options; the human confirms or edits them in a form. Ends your turn.", Schema(Obj(
                ("repo", Str("required when there is no target yet: the new GitHub repo name, lower-case (the owner is given)")),
                ("name", Str("lower-case letters and digits, starting with a letter")),
                ("resource", Str("PascalCase: the main thing the app manages")),
                ("db", Obj(("type", "string"), ("enum", new JsonArray("sqlite", "sqlserver", "postgres")))),
                ("ui", Obj(("type", "string"), ("enum", new JsonArray("react", "blazor", "blazor-maui", "none")))),
                ("events", Obj(("type", "boolean"))),
                ("auth", Obj(("type", "boolean"), ("description", "sign-in: Keycloak locally, Entra External ID in Azure; only with a web ui (not none)")))),
                "name", "resource", "db", "ui", "events")),
            Tool("submit_brief", "Show the human the whole brief for confirmation. Ends your turn; a correction comes back as the next message.", Schema(Obj(("markdown", Str())), "markdown")),
            Tool("submit_product", "New repo session only, in place of submit_brief: show the human the whole product document (the product template: purpose, users, a Home decision and 1-10 features) for confirmation. Ends your turn; a correction comes back as the next message.",
                Schema(Obj(("markdown", Str())), "markdown")),
        ];
    }

    public ToolAnswer Call(string name, JsonObject input)
    {
        if (_ui is not null) return new ToolAnswer($"your turn ended with {_ui}: stop now and wait for the human's answer", true);
        try
        {
            var s = files.Session(session) ?? throw new FactoryException($"no session {session}");
            if (UiTools.Contains(name)) return Ui(s, name, input);
            return new ToolAnswer(name switch
            {
                "read_file" => ReadFile(s, input),
                "list_dir" => ListDir(s, input),
                "grep" => Grep(s, input),
                "submit_specs" => SubmitSpecs(s, input),
                "revise_spec" => ReviseSpec(s, input),
                _ => throw new FactoryException($"unknown tool '{name}'"),
            });
        }
        catch (Exception e) when (e is FactoryException or IOException or UnauthorizedAccessException or ArgumentException or RegexParseException)
        {
            return new ToolAnswer(e.Message, true);
        }
    }

    // --- UI tools: recorded as pending, and the turn ends ---------------------------------------

    private ToolAnswer Ui(JsonObject s, string name, JsonObject input)
    {
        if (UiInputError(s, name, input) is { } bad) return new ToolAnswer(bad, true);
        s["pending"] = new JsonObject { ["id"] = null, ["name"] = name, ["input"] = input.DeepClone() };
        s["pendingResults"] = new JsonArray();
        files.SaveSession(session, s);
        _ui = name;
        return new ToolAnswer(Shown);
    }

    /// <summary>idea-turn.ps1's Test-UiInput: why a UI call is malformed, or null.</summary>
    internal static string? UiInputError(JsonObject s, string name, JsonObject input)
    {
        switch (name)
        {
            case "ask_questions":
                var q = input["questions"] as JsonArray ?? [];
                if (q.Count is < 1 or > 4) return "ask 1 to 4 questions";
                foreach (var x in q)
                    if (string.IsNullOrEmpty(Text(x?["question"])) || (x?["options"] as JsonArray)?.Count is null or < 2)
                        return "every question needs text and at least 2 options";
                return null;
            case "submit_brief":
                if (Text(s["kind"]) == "repo") return "this is a New repo session: call submit_product with the product document, not submit_brief";
                return string.IsNullOrEmpty(Text(input["markdown"])) ? "markdown is required" : null;
            case "submit_product":
                if (Text(s["kind"]) != "repo") return "submit_product is only for a New repo session; call submit_brief";
                return ProductText.Error(Text(input["markdown"]));
            case "propose_scaffold":
                foreach (var k in new[] { "name", "resource", "db", "ui" })
                    if (string.IsNullOrEmpty(Text(input[k]))) return $"{k} is required";
                if (string.IsNullOrEmpty(Text(s["target"])) && !RepoName().IsMatch(Text(input["repo"]) ?? ""))
                    return "repo is required: there is no target yet, so name a new lower-case GitHub repo";
                return null;
        }
        return null;
    }

    // --- read-only tools, fenced -----------------------------------------------------------------

    /// <summary>The session's clone (when it exists), then factory/templates.</summary>
    private List<string> Roots(JsonObject s)
    {
        var roots = new List<string>();
        if (Text(s["checkout"]) is { Length: > 0 } c && Directory.Exists(c)) roots.Add(Path.GetFullPath(c));
        roots.Add(files.TemplatesDir);
        return roots;
    }

    /// <summary>A model-supplied path, refused anywhere outside the target and the templates.</summary>
    private string Fenced(JsonObject s, string? path)
    {
        var roots = Roots(s);
        var full = string.IsNullOrEmpty(path) ? roots[0] : Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(roots[0], path));
        foreach (var root in roots)
        {
            var r = root.TrimEnd('\\', '/');
            if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return full;
        }
        throw new FactoryException($"'{path}' is outside the target and factory/templates");
    }

    private string Rel(JsonObject s, string full)
    {
        foreach (var root in Roots(s))
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Path.GetRelativePath(root, full).Replace('\\', '/');
        return full;
    }

    private string ReadFile(JsonObject s, JsonObject input)
    {
        var path = Text(input["path"]);
        var p = Fenced(s, path);
        if (!File.Exists(p)) throw new FactoryException($"no file '{path}'");
        if (new FileInfo(p).Length > MaxFile) throw new FactoryException($"'{path}' is larger than {MaxFile / 1024} KB; use grep");
        return File.ReadAllText(p);
    }

    private string ListDir(JsonObject s, JsonObject input)
    {
        var path = Text(input["path"]);
        var p = Fenced(s, path);
        if (!Directory.Exists(p)) throw new FactoryException($"no folder '{path}'");
        var entries = new DirectoryInfo(p).EnumerateFileSystemInfos()
            .Where(e => !Skip.Contains(e.Name))
            .OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.InvariantCultureIgnoreCase)
            .Select(e => e is DirectoryInfo ? e.Name + "/" : e.Name);
        return string.Join('\n', entries);
    }

    private string Grep(JsonObject s, JsonObject input)
    {
        var p = Fenced(s, Text(input["path"]));
        // Select-String, as idea-turn.ps1 used it, ignores case.
        var regex = new Regex(Text(input["pattern"]) ?? "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        var glob = Text(input["glob"]) is { Length: > 0 } g ? Glob(g) : null;
        IEnumerable<string> found = File.Exists(p) ? [p] : Walk(p);
        if (glob is not null) found = found.Where(f => glob.IsMatch(Path.GetFileName(f)));
        var hits = new List<string>();
        foreach (var file in found)
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (!regex.IsMatch(line)) continue;
                hits.Add($"{Rel(s, file)}:{n}: {line.Trim()}");
                if (hits.Count > MaxHits) break;
            }
            if (hits.Count > MaxHits) break;
        }
        if (hits.Count == 0) return "no matches";
        if (hits.Count > MaxHits) hits = [.. hits.Take(MaxHits), "(more matches not shown; narrow the search)"];
        return string.Join('\n', hits);
    }

    private static IEnumerable<string> Walk(string dir)
    {
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (e is DirectoryInfo d) { if (!Skip.Contains(d.Name)) foreach (var f in Walk(d.FullName)) yield return f; }
            else yield return e.FullName;
        }
    }

    /// <summary>PowerShell's -like: * and ? wildcards, case-insensitive.</summary>
    private static Regex Glob(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

    // --- spec tools -----------------------------------------------------------------------------

    private string SubmitSpecs(JsonObject s, JsonObject input)
    {
        var idea = Text(s["idea"]);
        if (string.IsNullOrEmpty(idea)) throw new FactoryException("the brief is not confirmed yet; submit_specs comes after the human confirms it");
        if ((s["specs"] as JsonArray ?? []).Any(x => !string.IsNullOrEmpty(Text(x?["decision"])))) throw new FactoryException("the human has started deciding specs; use revise_spec");
        var specs = input["specs"] as JsonArray ?? [];
        if (specs.Count is < 1 or > 5) throw new FactoryException("submit 1 to 5 specs");
        foreach (var sp in specs)
        {
            if (Title(Text(sp?["markdown"])) is null) throw new FactoryException($"spec {Text(sp?["k"])} has no '# Spec: <title>' line");
            RequireClearScope(Text(sp?["markdown"])!);
        }
        RequireHome(s, specs.Select(sp => Text(sp?["markdown"])!));
        var list = new JsonArray();
        foreach (var sp in specs)
        {
            var k = (int)Number(sp!["k"]);
            var md = Text(sp["markdown"])!;
            files.WriteText(Path.Combine(Home, "ideas", idea, $"spec-{k}.md"), md + Environment.NewLine);
            list.Add(new JsonObject { ["k"] = k, ["title"] = Title(md), ["decision"] = null, ["job"] = null });
        }
        s["specs"] = list;
        files.SaveSession(session, s);
        return $"wrote {specs.Count} spec(s); the human now decides each one in the panel";
    }

    private string ReviseSpec(JsonObject s, JsonObject input)
    {
        var k = Number(input["k"]);
        var entry = (s["specs"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(e => Number(e["k"]) == k)
            ?? throw new FactoryException($"there is no spec {Text(input["k"])}");
        if (Text(entry["decision"]) is { Length: > 0 } d) throw new FactoryException($"spec {Text(input["k"])} is already decided ({d})");
        var md = Text(input["markdown"]);
        if (Title(md) is not { } title) throw new FactoryException("the new markdown has no '# Spec: <title>' line");
        RequireClearScope(md!);
        var idea = Path.Combine(Home, "ideas", Text(s["idea"]) ?? "");
        RequireHome(s, (s["specs"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(e => Number(e["k"]) == k ? md! : files.Text(Path.Combine(idea, $"spec-{(int)Number(e["k"])}.md")) ?? ""));
        files.WriteText(Path.Combine(Home, "ideas", Text(s["idea"]) ?? "", $"spec-{(int)k}.md"), md + Environment.NewLine);
        entry["title"] = title;
        files.SaveSession(session, s);
        return $"spec {Text(input["k"])} revised";
    }

    /// <summary>A new app's spec set (the session scaffolded its target) must decide Home in one spec (contract, "Ideas").</summary>
    private static void RequireHome(JsonObject s, IEnumerable<string> specs)
    {
        // A repo's first feature (spec 22) carries PRODUCT.md's Home line in `home`.
        var home = Text(s["home"]);
        if (s["scaffold"] is not JsonObject && string.IsNullOrEmpty(home)) return;
        if (specs.Any(md => SpecText.HomeDecision(PsText.Lines(md)) is not null)) return;
        throw new FactoryException(string.IsNullOrEmpty(home)
            ? "this idea scaffolded a new app, so one spec must decide Home: add '## Home' with '- Home: replace|redirect|keep: <detail>', from the brief's Home"
            : $"this is the app's first feature, so one spec must decide Home: add '## Home' with '{home}', as docs/PRODUCT.md says");
    }

    /// <summary>A path written after other words on a Scope line is refused, so the model rewrites it (contract, "Targets").</summary>
    private static void RequireClearScope(string md)
    {
        if (SpecText.AmbiguousScope(PsText.Lines(md)) is { Count: > 0 } vague) throw new FactoryException(string.Join("; ", vague));
    }

    /// <summary>The state dir: sessions/&lt;S&gt;/ sits two folders below it.</summary>
    private string Home => Path.GetFullPath(Path.Combine(files.Dir(session), "..", ".."));

    internal static string? Title(string? md) => md is not null && SpecTitle().Match(md) is { Success: true } m ? m.Groups[1].Value : null;

    internal static string? Text(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() is JsonValueKind.Null => null,
        _ => n.ToJsonString(),
    };

    private static double Number(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>()
        : double.TryParse(Text(n), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

    // -notmatch, as idea-turn.ps1 used it, ignores case.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*$", RegexOptions.IgnoreCase)] private static partial Regex RepoName();
    [GeneratedRegex(@"^#\s*Spec:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)] private static partial Regex SpecTitle();
}
