using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Extensions;

namespace Factory.UnitTests;

public class IdeaTurnTests : CommandTestBase, IDisposable
{
    private readonly ILlmProvider _provider = Substitute.For<ILlmProvider>();
    private readonly IIdeaClaude _claude = Substitute.For<IIdeaClaude>();
    private readonly IStateSettings _state = Substitute.For<IStateSettings>();
    private readonly IStateSync _sync = Substitute.For<IStateSync>();
    private readonly Dictionary<string, string?> _vars = [];
    private readonly IReadOnlyDictionary<string, string?> _childEnv = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = null };
    private string[] _out = [];
    private (string File, IReadOnlyList<string> Args, string Work, string Stdin)? _ran;

    public IdeaTurnTests()
    {
        Paths.ScriptsDir.Returns(FactoryPaths.FindScripts(AppContext.BaseDirectory)!);
        _provider.ChildEnvironment().Returns(_childEnv);
        _claude.Run(default!, default!, default!, default!, default!, default!).ReturnsForAnyArgs(c =>
        {
            _ran = (c.ArgAt<string>(0), c.ArgAt<IReadOnlyList<string>>(1), c.ArgAt<string>(2), c.ArgAt<string>(4));
            foreach (var l in _out) c.ArgAt<Action<string>>(5)(l);
            return (_out.Length == 0 ? 1 : 0, "claude: not logged in");
        });
    }

    public void Dispose() { try { Directory.Delete(Path.Combine(Home, "sessions"), true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string SessionFile(string name) => Path.Combine(Home, "sessions", "S-001", name);

    private IdeaTurnCommand Turn() => new(new IdeaFiles(Paths, Files), Paths, _provider, DotEnv, _state, _sync, Substitute.For<IStateLeases>(), Options.Create(new StateOptions()), _claude,
        new CliEnvironment(_vars, "w", "\n", "appdata", Path.GetTempPath(), "bin", ["factory"]));

    private (int Code, string Err) Run(string id = "S-001")
    {
        var err = new StringWriter();
        var code = Turn().Run([id], TextReader.Null, new StringWriter(), err);
        return (code, err.ToString());
    }

    private void PutSession(string json = """{"id":"S-001","status":"idle","checkout":null,"costUsd":0.25,"pending":null}""",
        string chat = """{"messages":[{"role":"user","content":[{"type":"text","text":"Idea: tags"}]}]}""")
    {
        Files.Put(SessionFile("session.json"), json);
        Files.Put(SessionFile("chat.json"), chat);
    }

    private JsonObject Session() => JsonNode.Parse(Files.Text(SessionFile("session.json"))!)!.AsObject();

    private JsonArray Chat() => JsonNode.Parse(Files.Text(SessionFile("chat.json"))!)!["messages"]!.AsArray();

    private static readonly string[] Ok =
    [
        """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Hello"}]}}""",
        """{"type":"result","subtype":"success","is_error":false,"total_cost_usd":0.5,"session_id":"c-123"}""",
    ];

    [Fact]
    public void A_session_that_does_not_exist_is_exit_2()
    {
        Assert.Equal((2, "idea-turn: no session S-404" + Environment.NewLine), Run("S-404"));
        _claude.DidNotReceiveWithAnyArgs().Run(default!, default!, default!, default!, default!, default!);
    }

    [Fact]
    public void A_turn_runs_claude_on_the_run_modes_environment_and_records_the_reply_cost_and_claude_session()
    {
        PutSession();
        _out = Ok;

        Assert.Equal(0, Run().Code);

        var (file, args, work, stdin) = _ran!.Value;
        Assert.Equal(ClaudeCli.Executable(), file);
        Assert.Equal("Idea: tags", stdin);
        Assert.Equal(Path.Combine(Home, "sessions", "S-001"), work);
        Assert.Contains("--session-id", args);
        Assert.Equal("claude-opus-5-5", args[args.ToList().IndexOf("--model") + 1]);
        _claude.Received().Run(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string?>>(e => e.ContainsKey("ANTHROPIC_API_KEY") && e["ANTHROPIC_API_KEY"] == null), Arg.Any<string>(), Arg.Any<Action<string>>());
        var s = Session();
        Assert.Equal(("idle", null, "c-123", 0.75, "claude-opus-5-5"), ((string?)s["status"], (string?)s["error"], (string?)s["claudeSession"], (double)s["costUsd"]!, (string?)s["model"]));
        Assert.Equal("Hello", (string)Chat()[1]!["content"]![0]!["text"]!);
        Assert.EndsWith("""{"type":"end"}""" + "\n", Files.Text(SessionFile("stream.jsonl")));
    }

    [Fact]
    public void A_later_turn_resumes_its_claude_session_in_the_clone_with_the_new_user_content()
    {
        var clone = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "idea-clone-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        try
        {
            PutSession($$"""{"id":"S-001","status":"idle","checkout":"{{clone.Replace("\\", "\\\\")}}","claudeSession":"c-9","costUsd":0}""",
                """{"messages":[{"role":"user","content":[{"type":"text","text":"x"}]},{"role":"assistant","content":[{"type":"text","text":"y"}]},{"role":"user","content":[{"type":"text","text":"more please"}]}]}""");
            _out = Ok;

            Run();

            var (_, args, work, stdin) = _ran!.Value;
            Assert.Equal(("--resume", "c-9"), (args[^2], args[^1]));
            Assert.DoesNotContain("--session-id", args);
            Assert.Equal(("more please", clone), (stdin, work));
        }
        finally { Directory.Delete(clone, true); }
    }

    [Fact]
    public void FACTORY_IDEA_MODEL_overrides_the_model_and_FACTORY_IDEA_CLAUDE_CMD_replaces_claude()
    {
        PutSession();
        _out = Ok;
        DotEnv.Get("FACTORY_IDEA_MODEL").Returns("claude-sonnet-5-5");
        _vars["FACTORY_IDEA_CLAUDE_CMD"] = """["dotnet", "th.dll", "claude"]""";

        Run();

        var (file, args, _, _) = _ran!.Value;
        Assert.Equal("dotnet", file);
        Assert.Equal(["th.dll", "claude", "-p"], args.Take(3));
        Assert.Equal("claude-sonnet-5-5", (string?)Session()["model"]);
    }

    [Fact]
    public void A_mode_that_cannot_run_is_an_error_and_claude_is_not_started()
    {
        PutSession();
        _provider.ChildEnvironment().Returns(_ => throw new FactoryException("no FACTORY_FOUNDRY_RESOURCE"));

        Assert.Equal(0, Run().Code);

        _claude.DidNotReceiveWithAnyArgs().Run(default!, default!, default!, default!, default!, default!);
        Assert.Equal(("error", "no FACTORY_FOUNDRY_RESOURCE"), ((string?)Session()["status"], (string?)Session()["error"]));
        Assert.Contains("""{"type":"error","message":"no FACTORY_FOUNDRY_RESOURCE"}""", Files.Text(SessionFile("stream.jsonl")));
    }

    [Fact]
    public void A_claude_that_exits_without_a_result_or_with_an_error_result_leaves_the_session_in_error()
    {
        PutSession();
        Run();
        Assert.Equal("claude exited with code 1 and no result: claude: not logged in", (string?)Session()["error"]);

        PutSession();
        _out = ["""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"API Error: 529 overloaded","session_id":"c-1","total_cost_usd":0.01}"""];
        Run();
        Assert.Equal(("error", "API Error: 529 overloaded", "c-1"), ((string?)Session()["status"], (string?)Session()["error"], (string?)Session()["claudeSession"]));
    }

    [Fact]
    public void A_ui_call_the_mcp_server_recorded_during_the_turn_is_kept_with_its_tool_use_id()
    {
        PutSession();
        _out =
        [
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"q1","name":"mcp__factory__submit_brief","input":{"markdown":"# Brief: x"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"q1","content":"Shown to the human."}]}}""",
            """{"type":"result","is_error":false,"total_cost_usd":0,"session_id":"c-1"}""",
        ];
        _claude.Configure().Run(default!, default!, default!, default!, default!, default!).ReturnsForAnyArgs(c =>
        {
            // What the MCP server does while claude runs: pending recorded in session.json.
            var s = Session();
            s["pending"] = new JsonObject { ["id"] = null, ["name"] = "submit_brief", ["input"] = new JsonObject() };
            Files.Put(SessionFile("session.json"), s.ToJsonString());
            foreach (var l in _out) c.ArgAt<Action<string>>(5)(l);
            return (0, "");
        });

        Run();

        Assert.Equal(("q1", "submit_brief", "idle"), ((string?)Session()["pending"]!["id"], (string?)Session()["pending"]!["name"], (string?)Session()["status"]));
        Assert.Equal(2, Chat().Count);
    }

    [Fact]
    public void A_live_turn_holding_the_lock_is_exit_3()
    {
        PutSession();
        var lockFile = SessionFile("turn.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        File.WriteAllText(lockFile, Environment.ProcessId.ToString());

        Assert.Equal((3, "idea-turn: a turn is already running for S-001" + Environment.NewLine), Run());
        _claude.DidNotReceiveWithAnyArgs().Run(default!, default!, default!, default!, default!, default!);
    }

    [Fact]
    public void In_azure_the_session_is_pulled_first_and_pushed_after()
    {
        PutSession();
        _out = Ok;
        _state.IsAzure.Returns(true);
        var blobs = Substitute.For<IStateLeases>();
        blobs.Shared.Returns(true);
        blobs.Acquire("turn-S-001", 0).Returns("lease-1");
        var turn = new IdeaTurnCommand(new IdeaFiles(Paths, Files), Paths, _provider, DotEnv, _state, _sync, blobs, Options.Create(new StateOptions()), _claude,
            new CliEnvironment(_vars, "w", "\n", "appdata", Path.GetTempPath(), "bin", ["factory"]));

        turn.Run(["S-001"], TextReader.Null, new StringWriter(), new StringWriter());

        Received.InOrder(() => { _sync.Pull("sessions/S-001/"); _sync.Push(); });
        blobs.Received().Release("turn-S-001", "lease-1");
    }

    [Fact]
    public void A_turn_lease_held_on_another_machine_is_exit_3_and_says_so()
    {
        PutSession();
        _state.IsAzure.Returns(true);
        var blobs = Substitute.For<IStateLeases>();
        blobs.Shared.Returns(true);
        blobs.Acquire("turn-S-001", 0).Returns((string?)null);
        var err = new StringWriter();

        var code = new IdeaTurnCommand(new IdeaFiles(Paths, Files), Paths, _provider, DotEnv, _state, _sync, blobs, Options.Create(new StateOptions()), _claude,
            new CliEnvironment(_vars, "w", "\n", "appdata", Path.GetTempPath(), "bin", ["factory"])).Run(["S-001"], TextReader.Null, new StringWriter(), err);

        Assert.Equal((3, "idea-turn: a turn is already running for S-001 on another machine" + Environment.NewLine), (code, err.ToString()));
        Assert.False(File.Exists(SessionFile("turn.lock")));
        _claude.DidNotReceiveWithAnyArgs().Run(default!, default!, default!, default!, default!, default!);
    }

    [Fact]
    public void The_mcp_server_runs_on_the_turns_own_env_file()
    {
        Paths.Home.Returns("H");
        Paths.EnvFile.Returns("E.env");

        var env = Turn().McpConfig("S-001")["mcpServers"]!["factory"]!["env"]!;

        Assert.Equal(("H", "E.env"), ((string?)env["FACTORY_HOME"], (string?)env["FACTORY_ENV_FILE"]));
    }

    [Fact]
    public void The_mcp_server_is_this_cli_on_this_state_dir_and_claude_gets_only_its_tools()
    {
        Paths.Home.Returns("H");
        var mcp = Turn().McpConfig("S-001")["mcpServers"]!["factory"]!;

        Assert.Equal("""{"command":"factory","args":["idea-mcp","S-001"],"env":{"FACTORY_HOME":"H","FACTORY_SCRIPTS":""" + System.Text.Json.JsonSerializer.Serialize(Paths.ScriptsDir) + "}}",
            mcp.ToJsonString());
        var args = IdeaTurnCommand.Arguments("m", "sys.md", "mcp.json", "s", first: true);
        Assert.Equal(["--tools", "", "--strict-mcp-config", "--mcp-config", "mcp.json", "--allowedTools", "mcp__factory", "--setting-sources", ""],
            args.SkipWhile(a => a != "--tools").Take(9));
        Assert.DoesNotContain(args, a => a.Contains("Idea", StringComparison.Ordinal));
    }
}
