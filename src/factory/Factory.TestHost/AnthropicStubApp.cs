using System.Text;
using System.Text.Json.Nodes;

namespace Factory.TestHost;

/// <summary>
/// A stand-in for POST /v1/messages (stream: true), so idea sessions can be tested without a key (it was
/// factory/tests/fixtures/anthropic-stub.ps1). Serves &lt;dir&gt;/001.json, 002.json, ... in order, one per request, as
/// Messages API server-sent events, and saves each request body as &lt;dir&gt;/requests/NNN.json (its x-api-key in NNN.key,
/// and its Authorization, anthropic-beta and x-api-key headers in NNN.headers). A script file is
/// { "content": [ blocks ], "stop_reason": "...", "usage": { ... } }, where a block is { type: text, text } |
/// { type: thinking, thinking, signature } | { type: tool_use, id, name, input }, or { "status": &lt;code&gt;, "body": { ... } }
/// for an error. POST /__reset { "dir"? } starts over.
/// </summary>
public sealed class AnthropicStubApp(string dir, int delayMs = 0)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private string _dir = dir;
    private int _n;

    public async Task<StubReply> Handle(HttpContext ctx)
    {
        var (method, path) = (ctx.Request.Method, ctx.Request.Path.Value);
        if (method == "POST" && path == "/__reset")
        {
            var body = await StubHost.BodyText(ctx);
            if (body.Length > 0 && JsonNode.Parse(body)?["dir"]?.GetValue<string>() is { Length: > 0 } d) _dir = d;
            _n = 0;
            if (Directory.Exists(Path.Combine(_dir, "requests"))) Directory.Delete(Path.Combine(_dir, "requests"), true);
            return new(200);
        }
        if (method == "GET" && path == "/") return new(200);
        if (method != "POST" || path != "/v1/messages") return new(404);
        var name = $"{++_n:D3}";
        var requests = Directory.CreateDirectory(Path.Combine(_dir, "requests")).FullName;
        File.WriteAllText(Path.Combine(requests, $"{name}.json"), await StubHost.BodyText(ctx));
        var h = ctx.Request.Headers;
        File.WriteAllText(Path.Combine(requests, $"{name}.key"), h["x-api-key"].ToString());
        File.WriteAllText(Path.Combine(requests, $"{name}.headers"),
            new JsonObject { ["authorization"] = h.Authorization.ToString(), ["anthropic-beta"] = h["anthropic-beta"].ToString(), ["x-api-key"] = h["x-api-key"].ToString() }.ToJsonString());
        var file = Path.Combine(_dir, $"{name}.json");
        var script = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject()
            : new JsonObject { ["status"] = 500, ["body"] = new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = $"stub has no script {name}" } } };
        if (script["status"] is { } status) return StubReply.Json(status.GetValue<int>(), script["body"]?.DeepClone());
        await Stream(ctx, script);
        return new(200);
    }

    private async Task Stream(HttpContext ctx, JsonObject script)
    {
        var res = ctx.Response;
        res.StatusCode = 200;
        res.ContentType = "text/event-stream";
        var u = script["usage"] as JsonObject ?? [];
        int Usage(string key, int fallback) => u[key]?.GetValue<int>() ?? fallback;
        await Event(res, "message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = $"msg_stub_{_n}", ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(), ["stop_reason"] = null,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = Usage("input_tokens", 100), ["cache_creation_input_tokens"] = Usage("cache_creation_input_tokens", 0),
                    ["cache_read_input_tokens"] = Usage("cache_read_input_tokens", 0), ["output_tokens"] = 1,
                },
            },
        });
        var i = 0;
        foreach (var b in (script["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (b["type"]?.GetValue<string>())
            {
                case "text":
                    await Event(res, "content_block_start", Block(i, new JsonObject { ["type"] = "text", ["text"] = "" }));
                    foreach (var c in Chunks(b["text"]?.GetValue<string>() ?? "", 8)) await Event(res, "content_block_delta", Delta(i, new JsonObject { ["type"] = "text_delta", ["text"] = c }));
                    break;
                case "thinking":
                    await Event(res, "content_block_start", Block(i, new JsonObject { ["type"] = "thinking", ["thinking"] = "" }));
                    if (b["thinking"]?.GetValue<string>() is { Length: > 0 } t) await Event(res, "content_block_delta", Delta(i, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = t }));
                    await Event(res, "content_block_delta", Delta(i, new JsonObject { ["type"] = "signature_delta", ["signature"] = b["signature"]?.GetValue<string>() ?? "sig" }));
                    break;
                case "tool_use":
                    await Event(res, "content_block_start", Block(i, new JsonObject { ["type"] = "tool_use", ["id"] = b["id"]?.DeepClone(), ["name"] = b["name"]?.DeepClone(), ["input"] = new JsonObject() }));
                    var json = (b["input"] ?? new JsonObject()).ToJsonString();
                    foreach (var c in Chunks(json, Math.Max(1, (json.Length + 1) / 2))) await Event(res, "content_block_delta", Delta(i, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = c }));
                    break;
            }
            await Event(res, "content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = i });
            i++;
        }
        await Event(res, "message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = script["stop_reason"]?.GetValue<string>() ?? "end_turn", ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = Usage("output_tokens", 50) },
        });
        await Event(res, "message_stop", new JsonObject { ["type"] = "message_stop" });
    }

    private static JsonObject Block(int i, JsonObject block) => new() { ["type"] = "content_block_start", ["index"] = i, ["content_block"] = block };

    private static JsonObject Delta(int i, JsonObject delta) => new() { ["type"] = "content_block_delta", ["index"] = i, ["delta"] = delta };

    private async Task Event(HttpResponse res, string type, JsonObject data)
    {
        await res.Body.WriteAsync(Utf8.GetBytes($"event: {type}\ndata: {data.ToJsonString()}\n\n"));
        await res.Body.FlushAsync();
        if (delayMs > 0) await Task.Delay(delayMs);
    }

    private static IEnumerable<string> Chunks(string s, int size)
    {
        if (s.Length == 0) { yield return ""; yield break; }
        for (var i = 0; i < s.Length; i += size) yield return s.Substring(i, Math.Min(size, s.Length - i));
    }
}
