using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Core.Abstractions;
using Factory.Server.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Server;

/// <summary>
/// The idea-session endpoints (contract, "Idea sessions" and "Dashboard API"), as serve.ps1's New-Session and
/// Handle-Session. The conversation lives under sessions/&lt;S&gt;/; each assistant turn is a detached idea-turn.ps1 (until
/// spec 10 ports it). The .env is only checked for the mode's credential; no key is read into this process.
/// </summary>
public sealed partial class IdeaSessions(
    IFactoryCalls factory, IFactoryLauncher launcher, IFactoryPaths paths, IFileStore files, IServerSettings settings, BoardFile board,
    SessionFiles sessions, JobsApi jobs, IOptions<ServerOptions> options, TimeProvider clock)
{
    public Reply New(string body)
    {
        var req = JsonNode.Parse(body) as JsonObject ?? [];
        if (settings.IdeaBlocker is { } blocker) return Reply.Of(CallResult.Refuse(blocker));
        var text = (BoardFile.Str(req, "text") ?? "").Trim();
        if (text.Length == 0) return Reply.Of(CallResult.Refuse("describe the idea first"));
        var owner = settings.Env("GITHUB_OWNER");
        var target = BoardFile.Str(req, "target");
        bool isNew;
        if (Truthy(req["new"]))
        {
            // Add new: no repo yet; the model names it in propose_scaffold.
            if (owner is null) return Reply.Of(CallResult.Refuse($"Add new needs GITHUB_OWNER in {options.Value.EnvFile}"));
            target = null;
            isNew = true;
        }
        else
        {
            var r = factory.Run("check-target", target ?? "");
            if (!r.Ok) return Reply.Of(r);
            isNew = r.Message == "new";
        }

        var n = 0;
        // Every session folder holds at least its human.jsonl and chat.json.
        foreach (var d in files.Files(sessions.Root).Select(f => Path.GetFileName(Path.GetDirectoryName(f.Path))).Distinct())
            if (SessionId().Match(d ?? "") is { Success: true } m) n = Math.Max(n, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        var id = $"S-{n + 1:D3}";

        // A base: build on an unmerged job's branch (contract, "Stacked jobs").
        var baseJob = (BoardFile.Str(req, "base") ?? "").Trim() is { Length: > 0 } b ? b : null;
        if (baseJob is not null && isNew) return Reply.Of(CallResult.Refuse("base refused: a new repo has no branches to build on"));
        string? checkout = null;
        if (!isNew)
        {
            checkout = sessions.Clone(id);
            var r = baseJob is null ? factory.Run("clone-target", target!, checkout) : factory.Run("clone-target", target!, checkout, "-Base", baseJob);
            if (!r.Ok) return Reply.Of(r);
        }
        files.EnsureDirectory(sessions.Dir(id));
        var about = target is null
            ? $"This is a new target with no repo yet. Grill enough to know what it is, then call propose_scaffold, including repo: a short lower-case GitHub repo name. The owner is {owner}. A bootstrap job will create the repo and scaffold it from template-projects."
            : isNew ? "This is a new target: the GitHub repo does not exist yet (or is empty), and a bootstrap job will scaffold it from template-projects. Call propose_scaffold."
            : baseJob is not null ? $"This is an existing git repo on GitHub, cloned for you at branch factory/{baseJob}, not main: the unmerged work of {baseJob} ({BoardFile.Str(board.Job(baseJob), "title")}), which this idea builds on. main does not have that work yet, and the specs will be built on top of it. Read it with read_file, list_dir and grep."
            : "This is an existing git repo on GitHub, cloned for you; read it with read_file, list_dir and grep.";
        var s = new JsonObject
        {
            ["id"] = id, ["target"] = target, ["text"] = text, ["newTarget"] = isNew, ["base"] = baseJob, ["checkout"] = checkout,
            ["status"] = "idle", ["error"] = null, ["model"] = settings.IdeaModel, ["costUsd"] = 0, ["scaffold"] = null, ["idea"] = null,
            ["bootstrap"] = null, ["pending"] = null, ["pendingResults"] = new JsonArray(), ["specs"] = new JsonArray(),
            ["createdAt"] = clock.GetUtcNow().UtcDateTime.ToString("o"),
        };
        sessions.AddHuman(id, "idea", text, new JsonObject { ["target"] = target, ["newTarget"] = isNew });
        sessions.SaveChat(id, [User(TextBlock($"Target: {target ?? "(none yet)"}\n{about}\n\nIdea: {text}"))]);
        StartTurn(s);
        return Reply.Of(CallResult.Done(id));
    }

    /// <summary>A session action: reply, brief, write-specs, specs (POST a decision or PUT an edit of spec <paramref name="k"/>), close.</summary>
    public Reply Handle(string method, string id, string action, string? k, string body)
    {
        if (!sessions.Exists(id)) return Reply.NotFound("no such session");
        var s = sessions.Read(id);
        if (BoardFile.Str(s, "status") == "closed") return Reply.Of(CallResult.Refuse($"{id} is closed"));
        if (sessions.Busy(s)) return Reply.Of(CallResult.Refuse($"{id} is busy: wait for the current turn"));
        var req = method == "POST" ? (body.Length > 0 ? JsonNode.Parse(body) as JsonObject ?? [] : []) : null;
        var messages = sessions.ReadChat(id);
        return action switch
        {
            "reply" => Reply_(s, req!, messages),
            "brief" => Brief(s, messages),
            "write-specs" => WriteSpecs(s, messages),
            "specs" => Specs(s, method, k!, req, body),
            "close" => Close(s, req!),
            _ => Reply.NotFound(),
        };
    }

    private Reply Reply_(JsonObject s, JsonObject req, JsonArray messages)
    {
        var id = BoardFile.Str(s, "id")!;
        var text = (BoardFile.Str(req, "text") ?? "").Trim();
        var scaffold = req["scaffold"] as JsonObject;
        if (Truthy(scaffold) && string.IsNullOrEmpty(BoardFile.Str(s, "target")))
        {
            // Add new: the confirmed scaffold names the repo, which must not exist yet.
            var t = $"{settings.Env("GITHUB_OWNER")}/{BoardFile.Str(scaffold, "repo")}";
            var r = factory.Run("check-target", t);
            if (!r.Ok) return Reply.Of(r);
            if (r.Message != "new") return Reply.Of(CallResult.Refuse($"{t} already exists: pick it from the list instead"));
            s["target"] = t;
            text = $"{text} The target is {t}.".Trim();
        }
        if (Truthy(scaffold))
        {
            var kept = (JsonObject)scaffold!.DeepClone();
            kept.Remove("repo");
            s["scaffold"] = kept;
        }
        if (Truthy(s["pending"]))
        {
            sessions.AddHuman(id, "answer", text, new JsonObject
            {
                ["asked"] = new JsonObject { ["name"] = s["pending"]!["name"]?.DeepClone(), ["input"] = s["pending"]!["input"]?.DeepClone() },
            });
            AddAnswer(s, messages, text);
        }
        else if (text.Length > 0)
        {
            sessions.AddHuman(id, "message", text);
            AddUser(messages, [TextBlock(text)]);
        }
        else if (messages.Count == 0 || BoardFile.Str(messages[^1], "role") != "user") return Reply.Of(CallResult.Refuse("nothing to answer"));
        sessions.SaveChat(id, messages);
        StartTurn(s);
        return Reply.Of(CallResult.Done("turn started"));
    }

    private Reply Brief(JsonObject s, JsonArray messages)
    {
        var id = BoardFile.Str(s, "id")!;
        if (BoardFile.Str(s["pending"], "name") != "submit_brief") return Reply.Of(CallResult.Refuse("there is no brief to confirm"));
        if (Truthy(s["newTarget"]) && !Truthy(s["scaffold"])) return Reply.Of(CallResult.Refuse("confirm the scaffold options first"));
        var md = BoardFile.Str(s["pending"]!["input"], "markdown") ?? "";
        var title = BriefTitle().Match(md) is { Success: true } m ? m.Groups[1].Value : BoardFile.Str(s, "text") ?? "";
        var target = BoardFile.Str(s, "target")!;
        var r = Truthy(s["newTarget"]) ? factory.Run("new-idea", title, "-Target", target, "-Scaffold", s["scaffold"]!.ToJsonString(Reply.Json))
            : BoardFile.Str(s, "base") is { } b ? factory.Run("new-idea", title, "-Target", target, "-Base", b)
            : factory.Run("new-idea", title, "-Target", target);
        if (!r.Ok) return Reply.Of(r);
        var ids = r.Message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        s["idea"] = ids[0];
        if (ids.Length > 1) s["bootstrap"] = ids[1];
        // Set-Content: UTF-8 without a BOM, and a trailing newline.
        files.WriteAtomic(Path.Combine(paths.Home, "ideas", ids[0], "brief.md"), Encoding.UTF8.GetBytes(md + Environment.NewLine));
        if (BoardFile.Str(s, "bootstrap") is { } bootstrap)
        {
            AddAnswer(s, messages, $"The human confirmed the brief; it is saved as idea {ids[0]}. The target is being scaffolded by bootstrap job {bootstrap}. You will be told when it has merged; until then there is nothing to do.");
            sessions.SaveChat(id, messages);
            sessions.Save(s);
        }
        else
        {
            AddAnswer(s, messages, $"The human confirmed the brief; it is saved as idea {ids[0]}. Now be the spec-writer: read the target, then call submit_specs.");
            sessions.SaveChat(id, messages);
            StartTurn(s);
        }
        return Reply.Of(r);
    }

    private Reply WriteSpecs(JsonObject s, JsonArray messages)
    {
        var id = BoardFile.Str(s, "id")!;
        var ideaId = BoardFile.Str(s, "idea");
        var idea = ideaId is null ? null : board.Ideas().FirstOrDefault(i => BoardFile.Str(i, "id") == ideaId);
        if (idea is null) return Reply.Of(CallResult.Refuse("the brief is not confirmed yet"));
        if (BoardFile.Str(idea, "status") is var status && status != "drafting")
            return Reply.Of(CallResult.Refuse($"{ideaId} is {status}: merge the bootstrap job {BoardFile.Str(s, "bootstrap")} first"));
        var r = factory.Run("clone-target", BoardFile.Str(s, "target")!, sessions.Clone(id));
        if (!r.Ok) return Reply.Of(r);
        s["checkout"] = sessions.Clone(id);
        sessions.Save(s);
        AddUser(messages, [TextBlock($"The bootstrap job {BoardFile.Str(s, "bootstrap")} has merged, so {BoardFile.Str(s, "target")} now exists. Read it, then call submit_specs.")]);
        sessions.SaveChat(id, messages);
        StartTurn(s);
        return Reply.Of(CallResult.Done("turn started"));
    }

    private Reply Specs(JsonObject s, string method, string k, JsonObject? req, string body)
    {
        var n = int.Parse(k, System.Globalization.CultureInfo.InvariantCulture);
        var entry = (s["specs"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(e => e["k"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.GetValue<double>() == n);
        if (entry is null) return Reply.Of(CallResult.Refuse($"there is no spec {k}"));
        if (BoardFile.Str(entry, "decision") is { Length: > 0 } decided) return Reply.Of(CallResult.Refuse($"spec {k} is already decided ({decided})"));
        var file = Path.Combine(paths.Home, "ideas", BoardFile.Str(s, "idea") ?? "", $"spec-{k}.md");
        if (method == "PUT")
        {
            if (SpecTitle().Match(body) is not { Success: true } m) return Reply.Of(CallResult.Refuse("the spec needs a '# Spec: <title>' line"));
            entry["title"] = m.Groups[1].Value;
            files.WriteAtomic(file, Encoding.UTF8.GetBytes(body));
            sessions.Save(s);
            return Reply.Of(CallResult.Done("spec saved"));
        }
        var decision = BoardFile.Str(req, "decision");
        var r = decision switch
        {
            "rejected" => CallResult.Done(""),
            "approved" or "backlog" => factory.Run("add-spec", BoardFile.Str(s, "idea") ?? "", file),
            _ => CallResult.Refuse($"decision '{decision}' must be approved, backlog or rejected"),
        };
        if (!r.Ok) return Reply.Of(r);
        entry["decision"] = decision;
        if (decision != "rejected") entry["job"] = r.Message;
        if (decision == "approved")
        {
            var a = factory.Run("approve", r.Message);
            if (a.Ok) jobs.StartRunner();
            else { entry["decision"] = "backlog"; r = CallResult.Refuse($"{r.Message} is in Backlog: {a.Message}"); }
        }
        sessions.Save(s);
        return Reply.Of(r);
    }

    private Reply Close(JsonObject s, JsonObject req)
    {
        var id = BoardFile.Str(s, "id")!;
        var r = CallResult.Done("closed");
        if (BoardFile.Str(s, "idea") is { } idea)
        {
            r = Truthy(req["abandon"]) ? factory.Run("idea-abandon", idea)
                : Truthy(req["failed"]) ? factory.Run("idea-done", idea, "-Failed", "-Reason", BoardFile.Str(req, "reason") ?? "every spec rejected in session")
                : factory.Run("idea-done", idea);
            if (!r.Ok) return Reply.Of(r);
        }
        s["status"] = "closed";
        sessions.Save(s);
        try { files.DeleteDirectory(sessions.Clone(id)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Reply.Of(r);
    }

    private void StartTurn(JsonObject s)
    {
        s["status"] = "running";
        s["error"] = null;
        sessions.Save(s);
        // The server's FACTORY_HOME and FACTORY_ENV_FILE are the turn's (Program sets both for every child).
        launcher.StartDetached(["idea-turn", BoardFile.Str(s, "id")!]);
    }

    /// <summary>Adds user content: a new message, or more blocks in the last one when it is already the user's.</summary>
    private static void AddUser(JsonArray messages, JsonNode[] blocks)
    {
        if (messages.Count > 0 && messages[^1] is JsonObject last && BoardFile.Str(last, "role") == "user")
        {
            var content = last["content"] as JsonArray ?? [];
            if (last["content"] is not JsonArray) { content.Add(last["content"]?.DeepClone()); last["content"] = content; }
            foreach (var b in blocks) content.Add(b);
        }
        else messages.Add(User(blocks));
    }

    /// <summary>Answers the pending UI tool (with any server results from the same message) and clears it.</summary>
    private static void AddAnswer(JsonObject s, JsonArray messages, string text)
    {
        var blocks = (s["pendingResults"] as JsonArray ?? []).Select(b => b?.DeepClone()).ToList();
        blocks.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = s["pending"]?["id"]?.DeepClone(), ["content"] = text.Length > 0 ? text : "(no answer)" });
        AddUser(messages, [.. blocks!]);
        s["pending"] = null;
        s["pendingResults"] = new JsonArray();
    }

    private static JsonObject User(params JsonNode[] blocks) => new() { ["role"] = "user", ["content"] = new JsonArray(blocks) };

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    /// <summary>PowerShell truthiness, as serve.ps1's <c>if ($x)</c>: null, false, 0, "" and an empty object or array are false.</summary>
    internal static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonObject => true,
        JsonArray a => a.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => v.GetValue<double>() != 0,
            _ => false,
        },
        _ => false,
    };

    [GeneratedRegex(@"^S-(\d+)$", RegexOptions.IgnoreCase)] private static partial Regex SessionId();
    [GeneratedRegex(@"^#\s*Brief:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)] private static partial Regex BriefTitle();
    [GeneratedRegex(@"^#\s*Spec:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)] private static partial Regex SpecTitle();
}
