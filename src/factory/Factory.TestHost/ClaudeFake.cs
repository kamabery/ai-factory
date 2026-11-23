using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Factory.TestHost;

/// <summary>
/// <c>claude [--stream] [--exit &lt;n&gt;] [--url &lt;anthropic stub&gt;] [--state &lt;dir&gt;] -- &lt;claude's args&gt;</c>: stands in for claude, so tests spend no tokens.
/// <list type="bullet">
/// <item>With <c>--mcp-config</c> it is an idea turn's <c>claude -p ... --output-format stream-json</c>
/// (FACTORY_IDEA_CLAUDE_CMD; it was claude-stub.ps1): see <see cref="IdeaTurn"/>.</item>
/// <item>Otherwise it is a runner pass (FACTORY_CLAUDE_CMD): it exits <c>--exit</c> (default 0), and with <c>--stream</c>
/// first prints a stream-json pass, see <see cref="Pass"/>.</item>
/// </list>
/// </summary>
public static class ClaudeFake
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static int Run(IReadOnlyList<string> args)
    {
        var a = FakeArgs.Parse(args);
        Console.OutputEncoding = Utf8;
        if (a.Rest.Contains("--mcp-config"))
        {
            // A fake that breaks says why as claude would, in a result line, not as a stack trace the turn cuts to one line.
            try { return IdeaTurn(a); }
            catch (Exception e)
            {
                Out(new JsonObject { ["type"] = "result", ["subtype"] = "error_during_execution", ["is_error"] = true, ["result"] = $"claude fake: {e.GetType().Name}: {e.Message}", ["total_cost_usd"] = 0 });
                return 1;
            }
        }
        if (a.Options.ContainsKey("stream")) Pass();
        return int.Parse(a.Get("exit") ?? "0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A pass for the runner's tests: an init line, a stderr line, an assistant line in UTF-8 written while the job's log
    /// (FACTORY_HOME/jobs/FACTORY_JOB/run-1.jsonl) is held open for reading, as a reader tailing it would, a line of what it
    /// was given (key, resume, cwd, the first PATH folder, DOTNET_ROOT), and a result.
    /// </summary>
    private static void Pass()
    {
        Console.WriteLine("""{"type":"system","subtype":"init"}""");
        Console.Error.WriteLine("warn: from stderr");
        var log = Path.Combine(Environment.GetEnvironmentVariable("FACTORY_HOME") ?? "", "jobs", Environment.GetEnvironmentVariable("FACTORY_JOB") ?? "", "run-1.jsonl");
        using (File.Exists(log) ? new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null)
            Console.WriteLine("{\"type\":\"assistant\",\"text\":\"café ✓\"}");
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        Console.WriteLine($"key=[{Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")}] resume=[{Environment.GetEnvironmentVariable("FACTORY_CLAUDE_RESUME")}] cwd={Environment.CurrentDirectory}");
        Console.WriteLine($"path=[{path}] root=[{Environment.GetEnvironmentVariable("DOTNET_ROOT")}]");
        Console.WriteLine($"env-file=[{Environment.GetEnvironmentVariable("FACTORY_ENV_FILE")}] repo=[{Environment.GetEnvironmentVariable("FACTORY_REPO")}]");
        Console.WriteLine("""{"type":"result","total_cost_usd":0.25,"duration_ms":7}""");
    }

    private static readonly Dictionary<string, (double In, double Out, double Read, double Write)> Prices = new()
    {
        ["claude-opus-5-5"] = (4.0, 20.0, 0.20, 5.0),
        ["claude-sonnet-5-5"] = (2.0, 10.0, 0.20, 2.5),
    };

    private static readonly string[] Flags = ["-p", "--verbose", "--include-partial-messages", "--strict-mcp-config"];
    private static readonly string[] UiTools = ["ask_questions", "propose_scaffold", "submit_brief", "submit_product"];

    private static void Out(JsonNode o)
    {
        Console.Out.WriteLine(o.ToJsonString());
        Console.Out.Flush();
    }

    /// <summary>
    /// An idea turn. It takes claude's arguments and the prompt on stdin, starts the MCP server named in --mcp-config as
    /// claude would, and keeps each session's conversation in &lt;FACTORY_IDEA_STUB_STATE&gt;/&lt;session&gt;.json, so --resume
    /// continues it. Each model reply comes from the Anthropic stub (FACTORY_IDEA_STUB_URL), which serves scripted replies
    /// in order and saves every request: the request is the Messages API body claude would send (model, system, tools,
    /// messages), the tools named mcp__factory__&lt;name&gt;. Every tool_use runs through the MCP server, and the loop ends when
    /// a reply stops for anything but tool_use, or once a UI tool was shown. Prints stream-json as claude does: system init,
    /// stream_event text deltas, assistant and user messages, and a result with total_cost_usd at the model's list prices.
    /// </summary>
    private static int IdeaTurn(FakeArgs fake)
    {
        var args = fake.Rest;
        var opt = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var x = args[i];
            if (Flags.Contains(x)) { opt[x] = "true"; continue; }
            if (x.StartsWith("--", StringComparison.Ordinal))
                opt[x] = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
        }
        var model = opt.GetValueOrDefault("--model", "");
        var session = opt.GetValueOrDefault("--session-id") is { Length: > 0 } sid ? sid : opt.GetValueOrDefault("--resume", "");
        var stateDir = fake.Get("state", "FACTORY_IDEA_STUB_STATE") ?? Path.Combine(Path.GetTempPath(), "claude-stub");
        Directory.CreateDirectory(stateDir);
        var convo = Path.Combine(stateDir, $"{session}.json");
        var prompt = Console.In.ReadToEnd();

        // What this claude was given, for the tests: its arguments, its prompt, and the provider variables it saw.
        var env = new JsonObject();
        foreach (var n in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_FOUNDRY", "ANTHROPIC_FOUNDRY_RESOURCE",
                     "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL" })
            env[n] = Environment.GetEnvironmentVariable(n);
        File.WriteAllText(Path.Combine(stateDir, "last-run.json"), new JsonObject
        {
            ["args"] = new JsonArray([.. args.Select(s => (JsonNode?)s)]), ["prompt"] = prompt, ["cwd"] = Environment.CurrentDirectory, ["env"] = env,
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Utf8);

        int Fail(string msg)
        {
            Out(new JsonObject { ["type"] = "result", ["subtype"] = "error_during_execution", ["is_error"] = true, ["result"] = msg, ["session_id"] = session, ["total_cost_usd"] = 0 });
            return 1;
        }

        var messages = new JsonArray();
        if (opt.ContainsKey("--resume"))
        {
            if (!File.Exists(convo)) return Fail($"No conversation found with session ID: {session}");
            foreach (var m in JsonNode.Parse(File.ReadAllText(convo))!["messages"]!.AsArray()) messages.Add(m?.DeepClone());
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }) });

        // --- the MCP server, as claude starts it ---
        var cfg = JsonNode.Parse(File.ReadAllText(opt["--mcp-config"]))!["mcpServers"]!["factory"]!;
        var psi = new ProcessStartInfo((string)cfg["command"]!)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardInputEncoding = Utf8,
        };
        foreach (var x in cfg["args"]?.AsArray() ?? []) psi.ArgumentList.Add((string)x!);
        foreach (var (k, v) in cfg["env"]?.AsObject() ?? []) psi.Environment[k] = (string?)v;
        using var mcp = Process.Start(psi)!;
        var mcpErr = mcp.StandardError.ReadToEndAsync();
        var rpc = 0;
        JsonNode? Rpc(string method, JsonNode @params)
        {
            mcp.StandardInput.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++rpc, ["method"] = method, ["params"] = @params }.ToJsonString());
            mcp.StandardInput.Flush();
            var reply = mcp.StandardOutput.ReadLine() ?? throw new InvalidOperationException($"the MCP server exited: {mcpErr.GetAwaiter().GetResult()}");
            return JsonNode.Parse(reply)!["result"];
        }
        Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "claude-stub", ["version"] = "0" } });
        mcp.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        var tools = new JsonArray([.. Rpc("tools/list", new JsonObject())!["tools"]!.AsArray().Select(t => (JsonNode?)new JsonObject
        {
            ["name"] = $"mcp__factory__{t!["name"]}", ["description"] = t["description"]?.DeepClone(), ["input_schema"] = t["inputSchema"]?.DeepClone(),
        })]);
        Out(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = session, ["model"] = model, ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode?)(string)t!["name"]!)]) });

        // --- the model, through the Anthropic stub ---
        using var http = new HttpClient();
        var system = File.ReadAllText(opt["--system-prompt-file"], Utf8);
        var url = $"{(fake.Get("url", "FACTORY_IDEA_STUB_URL") ?? "").TrimEnd('/')}/v1/messages";
        var cost = 0.0;
        var last = "";
        for (var loop = 0; loop < 40; loop++)
        {
            var body = new JsonObject { ["model"] = model, ["max_tokens"] = 64000, ["stream"] = true, ["system"] = system, ["tools"] = tools.DeepClone(), ["messages"] = messages.DeepClone() };
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Utf8, "application/json") };
            using var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                string msg;
                try { msg = (string?)JsonNode.Parse(text)?["error"]?["message"] ?? text; }
                catch (System.Text.Json.JsonException) { msg = text; }
                return Fail($"API Error: {(int)resp.StatusCode} {msg}");
            }
            var (id, stop, usage, content) = Read(resp);
            if (Prices.TryGetValue(model, out var p))
                cost += (N(usage, "input_tokens") * p.In + N(usage, "output_tokens") * p.Out + N(usage, "cache_read_input_tokens") * p.Read + N(usage, "cache_creation_input_tokens") * p.Write) / 1e6;
            if (stop == "refusal") return Fail("the model declined to continue");
            foreach (var b in content)
                if ((string?)b["type"] == "tool_use" && !((string)b["name"]!).StartsWith("mcp__", StringComparison.Ordinal)) b["name"] = $"mcp__factory__{b["name"]}";
            // claude prints one assistant event per block, all with the message's id.
            foreach (var b in content)
                Out(new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["id"] = id, ["role"] = "assistant", ["content"] = new JsonArray(b.DeepClone()) }, ["session_id"] = session });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray([.. content.Select(b => (JsonNode?)b.DeepClone())]) });
            foreach (var b in content) if ((string?)b["type"] == "text") last = (string?)b["text"] ?? "";
            var uses = content.Where(b => (string?)b["type"] == "tool_use").ToList();
            if (stop != "tool_use" || uses.Count == 0) break;
            var results = new List<JsonObject>();
            foreach (var tu in uses)
            {
                var res = Rpc("tools/call", new JsonObject { ["name"] = ((string)tu["name"]!).Replace("mcp__factory__", ""), ["arguments"] = tu["input"]?.DeepClone() })!;
                var block = new JsonObject
                {
                    ["type"] = "tool_result", ["tool_use_id"] = tu["id"]?.DeepClone(), ["content"] = res["content"]?.DeepClone() ?? new JsonArray(),
                    ["is_error"] = (bool?)res["isError"] ?? false,
                };
                Out(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(block.DeepClone()) }, ["session_id"] = session });
                results.Add(block);
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]) });
            // A model that obeys "your turn is over" ends once a UI tool was shown, without asking for another reply.
            var shown = uses.Any(u => UiTools.Contains(((string)u["name"]!).Replace("mcp__factory__", ""))
                && !results.Any(r => (string?)r["tool_use_id"] == (string?)u["id"] && (bool)r["is_error"]!));
            if (shown) break;
        }

        mcp.StandardInput.Close();
        mcp.WaitForExit(5000);
        File.WriteAllText(convo, new JsonObject { ["messages"] = messages }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Utf8);
        Out(new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = last, ["session_id"] = session, ["total_cost_usd"] = cost });
        return 0;
    }

    private static double N(JsonObject usage, string key) => usage[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    /// <summary>One streamed reply: its id, stop reason, usage and content blocks, tool_use inputs parsed.</summary>
    private static (string Id, string? Stop, JsonObject Usage, List<JsonObject> Content) Read(HttpResponseMessage resp)
    {
        var blocks = new SortedDictionary<int, JsonObject>();
        var partial = new Dictionary<int, string>();
        var usage = new JsonObject();
        string? stop = null;
        using var reader = new StreamReader(resp.Content.ReadAsStream(), Utf8);
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var e = JsonNode.Parse(line[5..].Trim())!;
            var index = e["index"] is { } ix ? (int)ix : 0;
            switch ((string?)e["type"])
            {
                case "message_start":
                    usage = e["message"]?["usage"]?.DeepClone() as JsonObject ?? [];
                    break;
                case "content_block_start":
                    blocks[index] = e["content_block"]!.DeepClone().AsObject();
                    break;
                case "content_block_delta":
                    var b = blocks[index];
                    var d = e["delta"]!;
                    switch ((string?)d["type"])
                    {
                        case "text_delta":
                            b["text"] = (string?)b["text"] + (string?)d["text"];
                            Out(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = (string?)d["text"] } } });
                            break;
                        case "input_json_delta": partial[index] = partial.GetValueOrDefault(index, "") + (string?)d["partial_json"]; break;
                        case "thinking_delta": b["thinking"] = (string?)b["thinking"] + (string?)d["thinking"]; break;
                        case "signature_delta": b["signature"] = (string?)d["signature"]; break;
                    }
                    break;
                case "content_block_stop":
                    if ((string?)blocks[index]["type"] == "tool_use")
                        blocks[index]["input"] = partial.GetValueOrDefault(index) is { Length: > 0 } json ? JsonNode.Parse(json) : new JsonObject();
                    break;
                case "message_delta":
                    if ((string?)e["delta"]?["stop_reason"] is { Length: > 0 } s) stop = s;
                    if (e["usage"] is JsonObject u) foreach (var (k, v) in u) usage[k] = v?.DeepClone();
                    break;
            }
        }
        return ($"msg_stub_{Guid.NewGuid():N}"[..21], stop, usage, [.. blocks.Values]);
    }
}
