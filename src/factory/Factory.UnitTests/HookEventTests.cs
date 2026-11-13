using Factory.Core;

namespace Factory.UnitTests;

/// <summary>The plugin hook's payloads (factory/tests/fixtures/subagent-*.json, as Claude Code sends them).</summary>
public class HookEventTests
{
    private const string Start = """
        { "session_id": "abc123", "transcript_path": "C:/Users/me/.claude/projects/x/00893aaf.jsonl", "cwd": "D:/code/ai-factory.wt",
          "hook_event_name": "SubagentStart", "agent_id": "agent-abc123", "agent_type": "security-reviewer" }
        """;

    private const string PluginStart = """
        { "session_id": "00893aaf", "transcript_path": "C:/Users/me/.claude/projects/x/00893aaf.jsonl", "cwd": "D:/code/Scratchpad/todo-app.wt",
          "prompt_id": "caf75013", "agent_id": "a2f500956fbe5654a", "agent_type": "factory:builder", "hook_event_name": "SubagentStart" }
        """;

    private const string Stop = """
        { "session_id": "abc123", "transcript_path": "C:/Users/me/.claude/projects/x/abc123.jsonl", "hook_event_name": "SubagentStop",
          "stop_hook_active": false, "agent_id": "agent-abc123", "agent_type": "security-reviewer",
          "agent_transcript_path": "C:/Users/me/.claude/projects/x/abc123/subagents/agent-abc123.jsonl" }
        """;

    private const string InternalStop = """
        { "session_id": "abc123", "hook_event_name": "SubagentStop", "agent_id": "agent-internal", "agent_type": "" }
        """;

    [Fact]
    public void A_start_and_a_stop_are_agent_start_and_agent_stop_for_the_job()
    {
        var start = HookEvent.From(Start, "J-001", null)!;
        var stop = HookEvent.From(Stop, "J-001", null)!;

        Assert.Equal(["job", "kind", "agent", "agentId", "transcript"], start.Select(kv => kv.Key));
        Assert.Equal(("J-001", "agent-start", "security-reviewer"), ((string?)start["job"], (string?)start["kind"], (string?)start["agent"]));
        Assert.Equal("""{"job":"J-001","kind":"agent-stop","agent":"security-reviewer"}""", stop.ToJsonString());
    }

    [Fact]
    public void The_plugins_prefix_is_stripped_and_the_start_records_the_agent_id_and_its_transcript()
    {
        var e = HookEvent.From(PluginStart, "J-001", null)!;

        Assert.Equal("builder", (string?)e["agent"]);
        Assert.Equal("a2f500956fbe5654a", (string?)e["agentId"]);
        Assert.Equal(Path.GetFullPath("C:/Users/me/.claude/projects/x/00893aaf/subagents/agent-a2f500956fbe5654a.jsonl"), (string?)e["transcript"]);
    }

    [Fact]
    public void With_no_job_the_event_is_the_ideas_and_with_neither_there_is_none()
    {
        Assert.Equal("""{"idea":"I-001","kind":"agent-start","agent":"security-reviewer","agentId":"agent-abc123","transcript":""" + System.Text.Json.JsonSerializer.Serialize(Path.GetFullPath("C:/Users/me/.claude/projects/x/00893aaf/subagents/agent-agent-abc123.jsonl")) + "}",
            HookEvent.From(Start, null, "I-001")!.ToJsonString());
        Assert.Null(HookEvent.From(Start, null, null));
        Assert.Null(HookEvent.From(Start, "", ""));
    }

    [Theory]
    [InlineData(InternalStop)]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{ "hook_event_name": "PreToolUse", "agent_type": "builder" }""")]
    [InlineData("""{ "hook_event_name": "SubagentStart", "agent_type": 7 }""")]
    public void An_internal_agent_another_hook_or_garbage_appends_nothing(string payload) =>
        Assert.Null(HookEvent.From(payload, "J-001", null));

    [Fact]
    public void A_start_without_an_agent_id_or_transcript_records_neither()
    {
        var e = HookEvent.From("""{ "hook_event_name": "SubagentStart", "agent_type": "builder", "agent_id": "a1" }""", "J-001", null)!;

        Assert.Equal("""{"job":"J-001","kind":"agent-start","agent":"builder"}""", e.ToJsonString());
    }
}
