using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>idea-mcp &lt;S&gt;</c>: the idea session's tools as a stdio MCP server, named <c>factory</c>, started by each turn's
/// claude (contract, "Idea sessions", Tools). Newline-delimited JSON-RPC 2.0 on stdin and stdout; nothing else is
/// written to stdout. It lives for one turn, so a UI tool ends the tools for the rest of it.
/// </summary>
public sealed class IdeaMcpCommand(IdeaFiles files) : IStandaloneCommand
{
    public string Name => "idea-mcp";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count < 1 || args[0].Length == 0)
        {
            stderr.WriteLine("factory: usage: idea-mcp <session>");
            return 2;
        }
        var tools = new IdeaTools(files, args[0]);
        while (stdin.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { Send(stdout, null, null, Error(-32700, "parse error")); continue; }
            if (msg is null) continue;
            // A notification (initialized, cancelled) has no id and gets no answer.
            if (!msg.ContainsKey("id")) continue;
            var id = msg["id"]?.DeepClone();
            var method = IdeaTools.Text(msg["method"]);
            var p = msg["params"] as JsonObject ?? [];
            switch (method)
            {
                case "initialize":
                    Send(stdout, id, new JsonObject
                    {
                        ["protocolVersion"] = IdeaTools.Text(p["protocolVersion"]) ?? "2025-06-18",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "factory", ["version"] = "1" },
                    }, null);
                    break;
                case "tools/list":
                    Send(stdout, id, new JsonObject { ["tools"] = IdeaTools.Definitions() }, null);
                    break;
                case "tools/call":
                    var answer = tools.Call(IdeaTools.Text(p["name"]) ?? "", p["arguments"] as JsonObject ?? []);
                    Send(stdout, id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = answer.Text }),
                        ["isError"] = answer.IsError,
                    }, null);
                    break;
                case "ping":
                    Send(stdout, id, new JsonObject(), null);
                    break;
                default:
                    Send(stdout, id, null, Error(-32601, $"method not found: {method}"));
                    break;
            }
        }
        return 0;
    }

    private static JsonObject Error(int code, string message) => new() { ["code"] = code, ["message"] = message };

    private static void Send(TextWriter stdout, JsonNode? id, JsonObject? result, JsonObject? error)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error is not null) o["error"] = error; else o["result"] = result;
        stdout.WriteLine(o.ToJsonString(IdeaFiles.Compact));
        stdout.Flush();
    }
}
