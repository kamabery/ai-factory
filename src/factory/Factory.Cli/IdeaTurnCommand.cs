using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary>
/// <c>idea-turn &lt;S&gt;</c>: one assistant turn of a dashboard idea session (contract, "Idea sessions", Turns). It holds
/// turn.lock (and in azure the lease on locks/turn-&lt;S&gt;), runs one <c>claude -p</c> pass on the run mode's provider with
/// the idea tools as its only tools (<see cref="IdeaMcpCommand"/>), streams it into stream.jsonl, then appends the
/// turn to chat.json and its cost to session.json. Exit 2: no such session; 3: a turn is already running.
/// </summary>
public sealed class IdeaTurnCommand(
    IdeaFiles files, IFactoryPaths paths, ILlmProvider provider, IDotEnv dotEnv, IStateSettings state, IStateSync sync, IBlobClient blobs,
    IOptions<StateOptions> stateOptions, IIdeaClaude claude, CliEnvironment host) : IStandaloneCommand
{
    public const string DefaultModel = "claude-opus-5-5";

    public string Name => "idea-turn";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var id = args.Count > 0 ? args[0] : "";
        // In azure the session may have been started on another machine.
        if (state.IsAzure) Try(() => sync.Pull($"sessions/{id}/"), stderr, "state pull");
        if (id.Length == 0 || files.Session(id) is null)
        {
            stderr.WriteLine($"idea-turn: no session {id}");
            return 2;
        }
        var dir = files.Dir(id);
        using var held = new LeasedLock(Path.Combine(dir, "turn.lock"), $"turn-{id}", state, blobs, stateOptions).TryTake();
        if (held is null)
        {
            // A live local holder keeps turn.lock; a lease held elsewhere leaves none behind.
            var where = File.Exists(Path.Combine(dir, "turn.lock")) ? "" : " on another machine";
            stderr.WriteLine($"idea-turn: a turn is already running for {id}{where}");
            return 3;
        }

        var s = files.Session(id)!;
        var messages = files.Messages(id);
        var model = dotEnv.Get("FACTORY_IDEA_MODEL") ?? DefaultModel;
        s["status"] = "running";
        s["error"] = null;
        s["model"] = model;
        files.SaveSession(id, s);
        files.ResetStream(id);

        var stream = new IdeaStream(line => files.Stream(id, line));
        string? error = null;
        var temp = Path.Combine(host.TempPath, $"factory-idea-{id}-{Environment.ProcessId}");
        try
        {
            var env = new Dictionary<string, string?>(provider.ChildEnvironment());
            var resume = IdeaTools.Text(s["claudeSession"]);
            var first = string.IsNullOrEmpty(resume);
            Directory.CreateDirectory(temp);
            var system = Path.Combine(temp, "system-prompt.md");
            File.WriteAllText(system, IdeaPrompt.System(files.FactoryRoot));
            var mcp = Path.Combine(temp, "mcp.json");
            File.WriteAllText(mcp, McpConfig(id).ToJsonString(IdeaFiles.Indented));
            var claudeArgs = Arguments(model, system, mcp, first ? Guid.NewGuid().ToString() : resume!, first);
            var (file, argv) = host.Get("FACTORY_IDEA_CLAUDE_CMD") is { } fake
                ? ("pwsh", (IReadOnlyList<string>)["-NoProfile", "-File", fake, .. claudeArgs])
                : (ClaudeCli.Executable(), claudeArgs);
            var checkout = IdeaTools.Text(s["checkout"]);
            var work = checkout is { Length: > 0 } && Directory.Exists(checkout) ? checkout : dir;
            var (code, err) = claude.Run(file, argv, work, env, IdeaPrompt.Turn(messages, first), stream.Accept);
            error = Outcome(stream.Result, code, err);
        }
        catch (FactoryException e) { error = e.Message; }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        // The MCP server wrote pending and specs during the turn: build on its copy.
        var latest = files.Session(id) ?? s;
        foreach (var m in stream.Finish(latest)) messages.Add(m);
        latest["status"] = error is null ? "idle" : "error";
        latest["error"] = error;
        latest["model"] = model;
        if (IdeaTools.Text(stream.Result?["session_id"]) is { Length: > 0 } sid) latest["claudeSession"] = sid;
        latest["costUsd"] = Number(latest["costUsd"]) + Number(stream.Result?["total_cost_usd"]);
        files.SaveMessages(id, messages);
        files.SaveSession(id, latest);
        if (error is not null) files.Stream(id, new JsonObject { ["type"] = "error", ["message"] = error });
        files.Stream(id, new JsonObject { ["type"] = "end" });
        if (state.IsAzure) Try(() => sync.Push(), stderr, "state push");
        return 0;
    }

    /// <summary>claude's arguments (the prompt goes on stdin), as the contract lists them.</summary>
    internal static List<string> Arguments(string model, string systemPrompt, string mcpConfig, string session, bool first) =>
    [
        "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
        "--model", model, "--effort", "high", "--system-prompt-file", systemPrompt,
        "--tools", "", "--strict-mcp-config", "--mcp-config", mcpConfig, "--allowedTools", "mcp__factory",
        "--setting-sources", "",
        first ? "--session-id" : "--resume", session,
    ];

    /// <summary>The MCP server claude starts: this CLI's idea-mcp, on this state dir.</summary>
    internal JsonObject McpConfig(string session)
    {
        var self = host.SelfCommand;
        return new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["factory"] = new JsonObject
                {
                    ["command"] = self[0],
                    ["args"] = new JsonArray([.. self.Skip(1).Append("idea-mcp").Append(session).Select(a => (JsonNode)a)]),
                    ["env"] = new JsonObject { ["FACTORY_HOME"] = paths.Home, ["FACTORY_SCRIPTS"] = paths.ScriptsDir },
                },
            },
        };
    }

    /// <summary>Why the turn failed, or null: a result marked is_error, or no result at all.</summary>
    internal static string? Outcome(JsonObject? result, int code, string err)
    {
        if (result is null)
            return $"claude exited with code {code} and no result" + (err.Length > 0 ? $": {err.Split('\n')[^1].Trim()}" : "");
        if (result["is_error"] is JsonValue v && v.GetValueKind() == JsonValueKind.True)
            return IdeaTools.Text(result["result"]) is { Length: > 0 } text ? text : $"claude: {IdeaTools.Text(result["subtype"])}";
        return null;
    }

    private static double Number(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>()
        : double.TryParse(IdeaTools.Text(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static void Try(Action a, TextWriter stderr, string what)
    {
        try { a(); }
        catch (Exception e) when (e is FactoryException or HttpRequestException or IOException) { stderr.WriteLine($"idea-turn: {what} failed: {e.Message}"); }
    }
}
