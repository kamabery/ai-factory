using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.TestHost;

/// <summary>
/// <c>tool [--log &lt;file&gt;] [--fail &lt;regex&gt;|&lt;message&gt;] [--list &lt;json&gt;] -- &lt;args&gt;</c>: stands in for gh and az
/// (FACTORY_GH_CMD, FACTORY_AZ_CMD; it was tool-stub.ps1). Appends one JSON line per call to the log
/// (FACTORY_TOOL_LOG): { args, stdin, files, ghToken, ghTokenValue }, test tokens only, an <c>@path</c> argument's file
/// under files. A call whose arguments match the fail regex (FACTORY_TOOL_FAIL) prints the message and exits 1. Otherwise
/// a <c>list</c> answers the list JSON (FACTORY_TOOL_LIST, else <c>[]</c>), so a caller creates what it ensures, and the
/// rest answer <c>{}</c>.
/// </summary>
public static class ToolFake
{
    public static int Run(IReadOnlyList<string> args)
    {
        var a = FakeArgs.Parse(args);
        var stdin = FakeArgs.Stdin();
        if (a.Get("log", "FACTORY_TOOL_LOG") is { } log)
        {
            var files = a.Rest.Where(r => r.StartsWith('@') && File.Exists(r[1..])).Select(r => (JsonNode?)File.ReadAllText(r[1..]));
            var token = Environment.GetEnvironmentVariable("GH_TOKEN");
            var line = new JsonObject
            {
                ["args"] = new JsonArray([.. a.Rest.Select(r => (JsonNode?)r)]),
                ["stdin"] = stdin.Trim(),
                ["files"] = new JsonArray([.. files]),
                ["ghToken"] = !string.IsNullOrEmpty(token),
                ["ghTokenValue"] = token ?? "",
            };
            File.AppendAllText(log, line.ToJsonString() + "\n");
        }
        if (a.Get("fail", "FACTORY_TOOL_FAIL") is { } fail)
        {
            var parts = fail.Split('|', 2);
            if (Regex.IsMatch(string.Join(' ', a.Rest), parts[0], RegexOptions.IgnoreCase))
            {
                Console.WriteLine(parts.Length > 1 ? parts[1] : "");
                return 1;
            }
        }
        Console.WriteLine(a.Rest.Contains("list") ? a.Get("list", "FACTORY_TOOL_LIST") ?? "[]" : "{}");
        return 0;
    }
}
