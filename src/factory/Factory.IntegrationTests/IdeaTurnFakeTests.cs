using System.Text.Json.Nodes;
using Factory.Cli;
using Microsoft.AspNetCore.Builder;

namespace Factory.IntegrationTests;

/// <summary>
/// <c>idea-turn</c> end to end with FACTORY_IDEA_CLAUDE_CMD naming Factory.TestHost's claude fake: it starts the idea
/// tools' MCP server from the turn's --mcp-config, asks the Anthropic stub for the reply, and streams it back as claude does.
/// </summary>
public sealed class IdeaTurnFakeTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private WebApplication? _anthropic;
    private int _port;

    private string Home => _dir["state"];
    private string Scripts => _dir["anthropic"];

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Scripts);
        File.WriteAllText(Path.Combine(Scripts, "001.json"),
            """{"content":[{"type":"text","text":"What should a tag look like?"}],"stop_reason":"end_turn","usage":{"input_tokens":1000,"output_tokens":100}}""");
        _port = Factory.TestHost.StubHost.FreePort();
        _anthropic = Factory.TestHost.StubHost.Build(_port, new Factory.TestHost.AnthropicStubApp(Scripts).Handle);
        await _anthropic.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_anthropic is not null) await _anthropic.DisposeAsync();
        _dir.Dispose();
    }

    [Fact]
    public void A_turn_through_the_claude_fake_reaches_the_model_with_the_idea_tools_and_records_the_reply()
    {
        var session = Directory.CreateDirectory(Path.Combine(Home, "sessions", "S-001")).FullName;
        File.WriteAllText(Path.Combine(session, "session.json"), """{"id":"S-001","status":"idle","checkout":null,"costUsd":0,"pending":null}""");
        File.WriteAllText(Path.Combine(session, "chat.json"), """{"messages":[{"role":"user","content":[{"type":"text","text":"Idea: tags"}]}]}""");
        var envFile = _dir["idea.env"];
        File.WriteAllText(envFile, "");
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = Home, ["FACTORY_ENV_FILE"] = envFile, ["FACTORY_SECRET_DIR"] = _dir["secrets"], ["FACTORY_MODE"] = "local",
            ["FACTORY_SCRIPTS"] = GitHubStub.RepoFile("factory", "scripts"),
            ["FACTORY_IDEA_CLAUDE_CMD"] = Fakes.Command("claude", "--url", $"http://localhost:{_port}", "--state", _dir["claude-state"], "--"),
        };
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        // The MCP server is this CLI started again: its own build, which has its dependencies beside it.
        var code = CliHost.Run(["idea-turn", "S-001"], env, _dir.Path, stdout, stderr, null, ["dotnet", GitHubStub.RepoFile("artifacts", "bin", "Factory.Cli", "debug", "factory.dll")]);

        Assert.Equal((0, ""), (code, stderr.ToString().Trim()));
        var sent = Path.Combine(Scripts, "requests", "001.json");
        Assert.True(File.Exists(sent), File.ReadAllText(Path.Combine(session, "session.json")));
        var request = JsonNode.Parse(File.ReadAllText(sent))!;
        Assert.Equal("claude-opus-5-5", (string?)request["model"]);
        Assert.Contains(request["tools"]!.AsArray(), t => (string?)t!["name"] == "mcp__factory__ask_questions");
        var chat = JsonNode.Parse(File.ReadAllText(Path.Combine(session, "chat.json")))!["messages"]!.AsArray();
        Assert.Contains("What should a tag look like?", chat[^1]!.ToJsonString());
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(session, "session.json")))!;
        Assert.Equal(("idle", 0.006), ((string?)s["status"], Math.Round((double)s["costUsd"]!, 6)));
    }
}
