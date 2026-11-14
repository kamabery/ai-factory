using System.Text.Json.Nodes;

namespace Factory.IntegrationTests;

/// <summary>
/// The factory plugin (factory/plugin) as Claude Code loads it: it calls the CLI by name (contract, "Implementations"),
/// and its agents follow the rules the contract gives them.
/// </summary>
public sealed class PluginTests
{
    private static string Plugin(params string[] parts) => GitHubStub.RepoFile(["factory", "plugin", .. parts]);

    [Fact]
    public void Both_hooks_run_factory_event_with_the_payload_on_stdin()
    {
        var hooks = JsonNode.Parse(File.ReadAllText(Plugin("hooks", "hooks.json")))!["hooks"]!;

        foreach (var name in new[] { "SubagentStart", "SubagentStop" })
            Assert.Equal(["factory event"], hooks[name]!.AsArray().SelectMany(m => m!["hooks"]!.AsArray()).Select(h => (string?)h!["command"]));
    }

    [Fact]
    public void No_agent_skill_hook_or_prompt_calls_PowerShell_scripts()
    {
        var offending = Directory.EnumerateFiles(Plugin(), "*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f) is var t && (t.Contains(".ps1", StringComparison.Ordinal) || t.Contains("pwsh -NoProfile -File", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(Plugin(), f)).ToList();

        Assert.Empty(offending);
    }

    // A scaled-to-zero preview answers its first request 500 after about 45 s (J-027, "Backend call failure").
    [Theory, InlineData("approver"), InlineData("devops")]
    public void An_agent_checking_a_preview_retries_so_a_cold_start_is_not_an_escalation(string agent) =>
        Assert.Contains("curl -sf --retry 6 --retry-delay 10 --retry-all-errors", File.ReadAllText(Plugin("agents", $"{agent}.md")));

    // React's Static Web App puts the API's own address behind its auth, so that url answers 401 (J-027).
    [Theory, InlineData("approver"), InlineData("devops")]
    public void An_agent_checks_the_API_through_the_front_end_when_there_is_one(string agent) =>
        Assert.Contains("<front end>/api/openapi/v1.json", File.ReadAllText(Plugin("agents", $"{agent}.md")));
}
