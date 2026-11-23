using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>Spec 24: <c>platform status|start|sleep</c>, the platform Postgres server through az, and the sleep guard.</summary>
public class PlatformTests : CommandTestBase
{
    private const string Id = "/subscriptions/s/resourceGroups/rg-factory-platform/providers/Microsoft.DBforPostgreSQL/flexibleServers/psql-factory-1";
    private readonly IToolRunner _tools = Substitute.For<IToolRunner>();
    private readonly IPause _pause = Substitute.For<IPause>();
    private readonly List<string> _calls = [];
    private readonly Queue<string> _states = new();
    private ToolResult? _fail;

    public PlatformTests()
    {
        DotEnv.Get("HCP_ORG").Returns("org");
        DotEnv.Get("FACTORY_PLATFORM_SERVER_ID").Returns(Id);
        _tools.Az(default!).ReturnsForAnyArgs(c =>
        {
            var a = c.Arg<IReadOnlyList<string>>();
            if (_fail is not null) return _fail;
            _calls.Add(string.Join(' ', a));
            return a[2] == "show" ? new ToolResult(0, (_states.Count > 1 ? _states.Dequeue() : _states.Peek()) + "\n") : new ToolResult(0, "");
        });
    }

    private Platform Platform => new(DotEnv, _tools, _pause, Options.Create(new DeployOptions { PollSeconds = "0" }));

    private PlatformCommand Command => new(Platform, Files, Paths, Clock);

    private void State(params string[] states) { foreach (var s in states) _states.Enqueue(s); }

    private IEnumerable<string> Verbs => _calls.Select(c => c.Split(' ')[2]);

    // --- status -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Ready", "awake")]
    [InlineData("Stopped", "asleep")]
    [InlineData("Starting", "starting")]
    [InlineData("Stopping", "stopping")]
    [InlineData("Updating", "updating")]
    public void Status_maps_each_server_state(string state, string word)
    {
        State(state);
        Assert.Equal(word, Platform.Status());
        Assert.Equal($"postgres flexible-server show --ids {Id} --query state --output tsv", _calls.Single());
    }

    [Fact]
    public void With_deploys_off_there_is_no_platform()
    {
        DotEnv.Get("HCP_ORG").Returns((string?)null);
        Assert.Equal("platform refused: deploys are off", Assert.Throws<FactoryException>(() => Platform.Status()).Message);
        Assert.False(Platform.Enabled);
    }

    [Fact]
    public void Without_the_server_id_it_says_how_to_get_it()
    {
        DotEnv.Get("FACTORY_PLATFORM_SERVER_ID").Returns((string?)null);
        Assert.Equal("platform refused: no FACTORY_PLATFORM_SERVER_ID in .env; run factory previews-env",
            Assert.Throws<FactoryException>(() => Platform.Status()).Message);
    }

    [Fact]
    public void An_az_failure_is_a_refusal_with_its_first_error_line()
    {
        _fail = new ToolResult(1, "WARNING: noise\nERROR: Please run 'az login'.\nmore");
        Assert.Equal("platform refused: az show failed: ERROR: Please run 'az login'.",
            Assert.Throws<FactoryException>(() => Platform.Status()).Message);
    }

    // --- start ------------------------------------------------------------------------------------

    [Fact]
    public void Start_is_a_no_op_when_awake()
    {
        State("Ready");
        Assert.Equal("platform: awake", Platform.Start());
        Assert.Equal(["show"], Verbs);
    }

    [Fact]
    public void Start_starts_a_sleeping_server_and_az_waits_for_ready()
    {
        State("Stopped");
        Assert.Equal("platform: awake", Platform.Start());
        Assert.Equal(["show", "start"], Verbs);
        Assert.Equal($"postgres flexible-server start --ids {Id}", _calls[1]);
    }

    [Fact]
    public void Start_while_stopping_waits_for_stopped_first()
    {
        State("Stopping", "Stopping", "Stopped");
        Platform.Start();
        Assert.Equal(["show", "show", "show", "start"], Verbs);
        _pause.Received(2).Sleep(TimeSpan.Zero);
    }

    [Fact]
    public void Start_while_starting_waits_for_ready_without_starting_again()
    {
        State("Starting", "Ready");
        Assert.Equal("platform: awake", Platform.Start());
        Assert.Equal(["show", "show"], Verbs);
    }

    [Fact]
    public void The_command_starts_with_the_board_lock_released()
    {
        State("Stopped");
        var s = FakeSession.With();
        Assert.Equal(["platform: awake"], Command.Run(Args("platform", "start"), s).Out);
        Assert.Equal(1, s.Unlocks);
    }

    // --- sleep ------------------------------------------------------------------------------------

    [Fact]
    public void Sleep_stops_the_server_without_waiting()
    {
        State("Ready");
        Assert.Equal(["platform: stopping"], Command.Run(Args("platform", "sleep"), FakeSession.With()).Out);
        Assert.Equal($"postgres flexible-server stop --ids {Id} --no-wait", _calls[1]);
    }

    [Fact]
    public void Sleep_is_a_no_op_when_asleep()
    {
        State("Stopped");
        Assert.Equal(["platform: asleep"], Command.Run(Args("platform", "sleep"), FakeSession.With()).Out);
        Assert.Equal(["show"], Verbs);
    }

    public static TheoryData<string, string> Holders => new()
    {
        { "canary", "J-007 is in canary" },
        { "deploy", "J-007 is in deploy" },
    };

    [Theory]
    [MemberData(nameof(Holders))]
    public void Sleep_is_refused_while_a_job_is_in_canary_or_deploy(string stage, string holder)
    {
        State("Ready");
        var s = FakeSession.With(Jobs.New("J-006", "done"), Jobs.New("J-007", stage));
        Assert.Equal($"platform busy: {holder}", Assert.Throws<FactoryException>(() => Command.Run(Args("platform", "sleep"), s)).Message);
        Assert.DoesNotContain("stop", Verbs);
    }

    [Theory]
    [InlineData("waking")]
    [InlineData("applying")]
    [InlineData("up")]
    [InlineData("destroying")]
    public void Sleep_is_refused_while_a_preview_uses_the_server(string status)
    {
        State("Ready");
        var s = FakeSession.With(Jobs.New("J-008", "ready").With("deploy", new JsonObject { ["status"] = status, ["destroyAt"] = "2026-11-11T12:00:00Z" }));
        Assert.Equal($"platform busy: J-008's preview is {status}",
            Assert.Throws<FactoryException>(() => Command.Run(Args("platform", "sleep"), s)).Message);
    }

    [Theory]
    [InlineData("down", "2026-11-11T12:00:00Z")]
    [InlineData("failed", "2026-11-11T12:00:00Z")]
    [InlineData("up", "2026-11-10T11:00:00Z")]
    public void A_preview_that_is_down_failed_or_past_its_destroy_time_does_not_hold_it(string status, string destroyAt)
    {
        State("Ready");
        var s = FakeSession.With(Jobs.New("J-008", "ready").With("deploy", new JsonObject { ["status"] = status, ["destroyAt"] = destroyAt }));
        Assert.Equal(["platform: stopping"], Command.Run(Args("platform", "sleep"), s).Out);
    }

    [Theory]
    [InlineData("""{"status":"running"}""", "the canary is running")]
    [InlineData("""{"status":"pass","teardown":{"status":"destroying"}}""", "the canary is tearing down")]
    public void Sleep_is_refused_while_the_canary_uses_the_server(string canary, string holder)
    {
        State("Ready");
        Files.Put(Path.Combine(Home, "canary.json"), canary);
        Assert.Equal($"platform busy: {holder}",
            Assert.Throws<FactoryException>(() => Command.Run(Args("platform", "sleep"), FakeSession.With())).Message);
    }

    [Fact]
    public void A_finished_canary_with_its_teardown_down_does_not_hold_it()
    {
        State("Ready");
        Files.Put(Path.Combine(Home, "canary.json"), """{"status":"pass","teardown":{"status":"down"}}""");
        Assert.Equal(["platform: stopping"], Command.Run(Args("platform", "sleep"), FakeSession.With()).Out);
    }

    [Fact]
    public void An_unknown_verb_is_a_usage_refusal()
    {
        Assert.Equal("platform refused: usage: platform status|start|sleep",
            Assert.Throws<FactoryException>(() => Command.Run(Args("platform", "nap"), FakeSession.With())).Message);
    }
}
