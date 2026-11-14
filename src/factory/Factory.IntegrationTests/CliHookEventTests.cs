using Factory.Cli;

namespace Factory.IntegrationTests;

/// <summary><c>factory event</c> as the plugin's hook runs it: the payload on stdin, the job in FACTORY_JOB.</summary>
public sealed class CliHookEventTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Home => _dir["state"];

    private (int Code, string Out, string Err) Hook(string payload, Dictionary<string, string?> env, params string[] args)
    {
        env["FACTORY_HOME"] = Home;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CliHost.Run(["event", .. args], env, _dir.Path, stdout, stderr, new StringReader(payload));
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string Events => File.Exists(Path.Combine(Home, "events.jsonl")) ? File.ReadAllText(Path.Combine(Home, "events.jsonl")) : "";

    public CliHookEventTests()
    {
        Directory.CreateDirectory(Home);
        File.WriteAllText(Path.Combine(Home, "board.json"), """
            { "version": 2, "ideas": [], "jobs": [ { "id": "J-001", "stage": "build", "round": 2, "title": "t", "flag": null } ] }
            """);
    }

    [Fact]
    public void A_subagent_start_on_stdin_appends_agent_start_with_the_jobs_round_and_prints_nothing()
    {
        var r = Hook("""{ "hook_event_name": "SubagentStart", "agent_type": "factory:builder", "agent_id": "a1", "transcript_path": "C:/t/s.jsonl" }""",
            new() { ["FACTORY_JOB"] = "J-001" });

        Assert.Equal((0, "", ""), r);
        Assert.Contains("\"job\":\"J-001\",\"kind\":\"agent-start\",\"agent\":\"builder\",\"agentId\":\"a1\"", Events);
        Assert.Contains("\"round\":2", Events);
    }

    [Fact]
    public void Garbage_no_job_or_a_refused_event_exits_0_silently_and_appends_nothing()
    {
        Assert.Equal((0, "", ""), Hook("not json", new() { ["FACTORY_JOB"] = "J-001" }));
        Assert.Equal((0, "", ""), Hook("""{ "hook_event_name": "SubagentStop", "agent_type": "builder" }""", new()));
        // A board lock that cannot be taken refuses the event; the hook still says nothing.
        File.WriteAllText(Path.Combine(Home, "board.json"), "{ not a board");
        Assert.Equal((0, "", ""), Hook("""{ "hook_event_name": "SubagentStop", "agent_type": "builder" }""", new() { ["FACTORY_JOB"] = "J-001" }));

        Assert.Equal("", Events);
    }

    [Fact]
    public void Event_with_arguments_is_still_the_board_command()
    {
        var r = Hook("", new(), "-Kind", "nope");

        Assert.Equal((1, "", "factory: unknown event kind 'nope'" + Environment.NewLine), (r.Code, r.Out, r.Err));
    }
}
