using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Store;
using NSubstitute;

namespace Factory.UnitTests;

public class IdeaMcpTests : CommandTestBase
{
    public IdeaMcpTests() => Paths.ScriptsDir.Returns(FactoryPaths.FindScripts(AppContext.BaseDirectory)!);

    private string SessionFile => Path.Combine(Home, "sessions", "S-001", "session.json");

    private List<JsonObject> Talk(params string[] requests)
    {
        var stdout = new StringWriter();
        Assert.Equal(0, new IdeaMcpCommand(new IdeaFiles(Paths, Files)).Run(["S-001"], new StringReader(string.Join("\n", requests)), stdout, new StringWriter()));
        return [.. stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject())];
    }

    private static string Call(int id, string name, string args) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{name}}}","arguments":{{{args}}}}}""";

    [Fact]
    public void Answers_initialize_and_lists_the_eight_tools_and_ignores_notifications()
    {
        var r = Talk("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":"p","method":"ping"}""");

        Assert.Equal(3, r.Count);
        Assert.Equal(("2025-06-18", "factory"), ((string?)r[0]["result"]!["protocolVersion"], (string?)r[0]["result"]!["serverInfo"]!["name"]));
        Assert.Equal(["read_file", "list_dir", "grep", "submit_specs", "revise_spec", "ask_questions", "propose_scaffold", "submit_brief"],
            r[1]["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!));
        Assert.All(r[1]["result"]!["tools"]!.AsArray(), t => Assert.Equal("object", (string?)t!["inputSchema"]!["type"]));
        Assert.Equal("p", (string?)r[2]["id"]);
    }

    [Fact]
    public void An_unknown_method_and_a_bad_line_are_errors()
    {
        var r = Talk("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}""", "{not json");

        Assert.Equal((-32601, -32700), ((int)r[0]["error"]!["code"]!, (int)r[1]["error"]!["code"]!));
    }

    [Fact]
    public void A_valid_ui_call_is_recorded_pending_and_every_call_after_it_in_the_turn_is_refused()
    {
        Files.Put(SessionFile, """{"id":"S-001","target":"a/b","pending":null}""");

        var r = Talk(Call(1, "submit_brief", """{"markdown":"# Brief: x"}"""), Call(2, "list_dir", "{}"));

        Assert.Equal((false, IdeaTools.Shown), ((bool)r[0]["result"]!["isError"]!, (string?)r[0]["result"]!["content"]![0]!["text"]));
        Assert.Equal((true, "your turn ended with submit_brief: stop now and wait for the human's answer"),
            ((bool)r[1]["result"]!["isError"]!, (string?)r[1]["result"]!["content"]![0]!["text"]));
        var s = JsonNode.Parse(Files.Text(SessionFile)!)!;
        Assert.Equal("""{"id":null,"name":"submit_brief","input":{"markdown":"# Brief: x"}}""", s["pending"]!.ToJsonString());
    }

    [Theory]
    [InlineData("ask_questions", """{"questions":[]}""", "ask 1 to 4 questions")]
    [InlineData("ask_questions", """{"questions":[{"question":"q","options":[{"label":"a"}]}]}""", "every question needs text and at least 2 options")]
    [InlineData("submit_brief", "{}", "markdown is required")]
    [InlineData("propose_scaffold", """{"name":"t","resource":"T","db":"sqlite"}""", "ui is required")]
    [InlineData("propose_scaffold", """{"name":"t","resource":"T","db":"sqlite","ui":"react","repo":"Bad Name"}""", "repo is required: there is no target yet, so name a new lower-case GitHub repo")]
    public void A_malformed_ui_call_is_an_error_the_model_can_fix_and_records_nothing(string tool, string args, string error)
    {
        Files.Put(SessionFile, """{"id":"S-001","target":null,"pending":null}""");

        var r = Talk(Call(1, tool, args), Call(2, "submit_brief", """{"markdown":"ok"}"""));

        Assert.Equal((true, error), ((bool)r[0]["result"]!["isError"]!, (string?)r[0]["result"]!["content"]![0]!["text"]));
        Assert.False((bool)r[1]["result"]!["isError"]!);
    }

    [Fact]
    public void An_unknown_tool_or_a_missing_session_is_an_error_result()
    {
        Assert.Equal("no session S-001", (string?)Talk(Call(1, "list_dir", "{}"))[0]["result"]!["content"]![0]!["text"]);

        Files.Put(SessionFile, """{"id":"S-001"}""");
        Assert.Equal("unknown tool 'rm_rf'", (string?)Talk(Call(1, "rm_rf", "{}"))[0]["result"]!["content"]![0]!["text"]);
    }

    [Fact]
    public void Without_a_session_argument_it_refuses_to_start()
    {
        var err = new StringWriter();
        Assert.Equal(2, new IdeaMcpCommand(new IdeaFiles(Paths, Files)).Run([], TextReader.Null, new StringWriter(), err));
        Assert.Contains("usage: idea-mcp <session>", err.ToString());
    }
}
