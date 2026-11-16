using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Runner;
using Factory.Runner.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;
using NSubstitute.Extensions;

namespace Factory.UnitTests;

public class RunnerLoopTests : CommandTestBase
{
    /// <summary>factory.ps1 as the runner sees it: a queue, each job's stage, and every call recorded.</summary>
    private sealed class FakeFactory : IFactoryCommands
    {
        public readonly List<string> Queue = [];
        public readonly Dictionary<string, JsonObject> Jobs = [];
        public readonly List<string> Calls = [];
        public readonly Dictionary<string, FactoryResult> Answers = [];
        public string? Canary;

        public JsonObject Add(string id, string stage = "approved")
        {
            Queue.Add(id);
            return Jobs[id] = new JsonObject { ["id"] = id, ["stage"] = stage, ["resume"] = null, ["resumeSession"] = null };
        }

        public void Stage(string id, string stage) => Jobs[id]["stage"] = stage;

        public FactoryResult Run(params string[] args)
        {
            var call = string.Join(' ', args);
            Calls.Add(call);
            // A drain that never ends (say, a canary asked for forever) fails the test instead of eating the machine.
            if (Calls.Count > 1000) throw new InvalidOperationException("runaway drain: over 1000 factory.ps1 calls");
            foreach (var (prefix, answer) in Answers)
                if (call.StartsWith(prefix, StringComparison.Ordinal)) return answer;
            switch (args[0])
            {
                case "next":
                    if (Canary is not null && args.Contains("-NeedsCanary")) return new FactoryResult(0, "canary", "");
                    return new FactoryResult(0, Queue.FirstOrDefault(id => (string?)Jobs[id]["stage"] == "approved") ?? "", "");
                case "status":
                    return new FactoryResult(0, new JsonObject { ["jobs"] = new JsonArray([.. Jobs.Values.Select(j => j.DeepClone())]) }.ToJsonString(), "");
                case "set-stage":
                    Stage(args[1], args[2]);
                    return new FactoryResult(0, "", "");
                case "checkout":
                    return new FactoryResult(0, Path.Combine(Path.GetTempPath(), "wt", args[1]), "");
                case "canary":
                    return Canary == "pass" ? new FactoryResult(0, "", "") : new FactoryResult(1, "", $"factory: canary failed: {Canary}");
                default:
                    return new FactoryResult(0, "", "");
            }
        }
    }

    private readonly FakeFactory _factory = new();
    private readonly IRunnerLock _lock = Substitute.For<IRunnerLock>();
    private readonly IClaudeRunner _claude = Substitute.For<IClaudeRunner>();
    private readonly ILlmProvider _provider = Substitute.For<ILlmProvider>();
    private readonly IRunnerStarter _starter = Substitute.For<IRunnerStarter>();
    private readonly List<string> _log = [];
    private readonly IReadOnlyDictionary<string, string?> _childEnv = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = null };

    public RunnerLoopTests()
    {
        _lock.TryTake().Returns(Substitute.For<IDisposable>());
        _provider.ChildEnvironment().Returns(_childEnv);
        Paths.ScriptsDir.Returns(Path.Combine(Path.GetTempPath(), "fac", "factory", "scripts"));
        var log = Substitute.For<IRunnerLog>();
        log.When(l => l.Write(Arg.Any<string>())).Do(c => _log.Add(c.Arg<string>()));
        Sut = new RunnerLoop(_lock, _factory, _claude, _provider, log, Paths, Files, _starter, Microsoft.Extensions.Options.Options.Create(new RunnerOptions()));
    }

    private RunnerLoop Sut { get; }

    /// <summary>claude, as one pass moves the job: to <paramref name="stage"/>, with <paramref name="result"/> as its result line.</summary>
    private void Claude(string stage, string? result = """{"type":"result","total_cost_usd":0.5,"duration_ms":2000}""", int code = 0) =>
        _claude.Configure().Run(default!, default!, default!, default!, default!, default!).ReturnsForAnyArgs(c =>
        {
            _factory.Stage(c.ArgAt<string>(0), stage);
            return new ClaudeRun(code, result is null ? null : JsonNode.Parse(result)!.AsObject());
        });

    private int Index(string call) => _factory.Calls.IndexOf(call);

    [Fact]
    public void Drains_the_queue_in_order_publishing_each_finished_job_and_sweeps_when_drained()
    {
        _factory.Add("J-001");
        _factory.Add("J-002");
        Claude(Stages.Ready);

        Assert.Equal("drained", Sut.Run());

        Assert.True(Index("set-stage J-001 build -By runner") < Index("checkout J-001"));
        Assert.True(Index("checkout J-001") < Index("set-stage J-002 build -By runner"));
        Assert.Contains("set-cost J-001 0.5 2000", _factory.Calls);
        Assert.Contains("publish J-001", _factory.Calls);
        Assert.Contains("lesson J-002", _factory.Calls);
        Assert.Equal("sweep", _factory.Calls[^1]);
        Assert.Equal([$"start pid={Environment.ProcessId}", "take J-001", "claude J-001 exit=0 result=yes", "cost J-001 usd=0.5 ms=2000 in=0 out=0", "stage J-001 ready",
            "take J-002", "claude J-002 exit=0 result=yes", "cost J-002 usd=0.5 ms=2000 in=0 out=0", "stage J-002 ready", "exit drained"], _log);
        _claude.Received(1).Run("J-001", Arg.Is<string>(p => p == $"/factory:factory run J-001 {Paths.Home.Replace('\\', '/')} {Path.Combine(Path.GetTempPath(), "fac", "factory", "templates").Replace('\\', '/')}"),
            Path.Combine(Path.GetTempPath(), "wt", "J-001"), JobFile("J-001", "run-1.jsonl"), _childEnv, "");
    }

    [Fact]
    public void Does_nothing_while_another_runner_holds_the_lock()
    {
        _lock.TryTake().Returns((IDisposable?)null);

        Assert.Equal("locked", Sut.Run());

        Assert.Empty(_factory.Calls);
        Assert.Empty(_log);
    }

    [Fact]
    public void The_next_run_log_is_numbered_after_the_ones_there()
    {
        _factory.Add("J-001");
        Files.Put(JobFile("J-001", "run-1.jsonl"), "{}");
        Files.Put(JobFile("J-001", "round-1/run-9.jsonl"), "{}");
        Claude(Stages.Ready);

        Sut.Run();

        _claude.Received(1).Run("J-001", Arg.Any<string>(), Arg.Any<string>(), JobFile("J-001", "run-2.jsonl"), Arg.Any<IReadOnlyDictionary<string, string?>>(), "");
    }

    [Fact]
    public void Runs_the_canary_once_before_a_deployable_job_and_logs_how_it_went()
    {
        _factory.Add("J-001");
        _factory.Canary = "pass";
        Claude(Stages.Ready);

        Sut.Run();

        Assert.Equal(["next -NeedsCanary", "canary", "next"], _factory.Calls.Take(3));
        Assert.Equal("canary pass", _log[1]);

        _log.Clear();
        _factory.Calls.Clear();
        _factory.Canary = "Error: boom";
        new RunnerLoop(_lock, _factory, _claude, _provider, LogTo(_log), Paths, Files, _starter, Microsoft.Extensions.Options.Options.Create(new RunnerOptions())).Run();
        Assert.Equal("canary failed: Error: boom", _log[1]);
        Assert.Single(_factory.Calls, c => c == "canary");
    }

    private static IRunnerLog LogTo(List<string> lines)
    {
        var log = Substitute.For<IRunnerLog>();
        log.When(l => l.Write(Arg.Any<string>())).Do(c => lines.Add(c.Arg<string>()));
        return log;
    }

    [Fact]
    public void Logs_what_the_sweep_started_or_why_it_failed_without_failing_the_drain()
    {
        _factory.Answers["sweep"] = new FactoryResult(0, "J-004\ncanary", "");
        Assert.Equal("drained", Sut.Run());
        Assert.Contains("sweep started: J-004 canary", _log);

        _factory.Answers["sweep"] = new FactoryResult(1, "", "factory: hcp down");
        Assert.Equal("drained", Sut.Run());
        Assert.Contains("sweep failed: factory: hcp down", _log);
    }

    [Fact]
    public void A_provider_that_cannot_run_the_job_escalates_it_without_a_checkout_or_claude()
    {
        _factory.Add("J-001");
        _provider.ChildEnvironment().Returns(_ => throw new FactoryException("no FACTORY_FOUNDRY_RESOURCE"));

        Sut.Run();

        Assert.Contains("set-stage J-001 ready -By runner -Reason escalated -Detail provider: no FACTORY_FOUNDRY_RESOURCE", _factory.Calls);
        Assert.DoesNotContain("checkout J-001", _factory.Calls);
        Assert.Contains("provider J-001 failed: provider: no FACTORY_FOUNDRY_RESOURCE", _log);
        _claude.DidNotReceiveWithAnyArgs().Run(default!, default!, default!, default!, default!, default!);
    }

    [Fact]
    public void A_checkout_that_fails_escalates_with_factory_ps1s_reason()
    {
        _factory.Add("J-001");
        _factory.Answers["checkout J-001"] = new FactoryResult(1, "", "factory: clone of o/r failed: x");

        Sut.Run();

        Assert.Contains("set-stage J-001 ready -By runner -Reason escalated -Detail work dir: factory: clone of o/r failed: x", _factory.Calls);
        Assert.Contains("checkout J-001 failed: factory: clone of o/r failed: x", _log);
    }

    [Fact]
    public void A_resumed_job_continues_its_session_in_the_stage_it_stopped_in_and_a_redeployed_one_at_deploy()
    {
        var j = _factory.Add("J-001");
        j["resume"] = "review";
        j["resumeSession"] = "sess-1";
        var k = _factory.Add("J-002");
        k["resume"] = "deploy";
        Claude(Stages.Ready);

        Sut.Run();

        Assert.Contains("set-stage J-001 review -By runner", _factory.Calls);
        Assert.Contains("set-stage J-002 deploy -By runner", _factory.Calls);
        _claude.Received(1).Run("J-001", Arg.Is<string>(p => p.StartsWith("A usage limit stopped this run", StringComparison.Ordinal)), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>>(), "sess-1");
    }

    [Fact]
    public void A_usage_limit_flags_the_job_with_its_session_and_stops_the_drain_without_publishing()
    {
        _factory.Add("J-001");
        _factory.Add("J-002");
        Claude(Stages.Build, """{"type":"result","is_error":true,"api_error_status":429,"api_error":"usage_limit_reached","result":"5-hour limit","session_id":"sess-027","total_cost_usd":0.38,"duration_ms":10}""", 1);

        Assert.Equal("usage-limit", Sut.Run());

        Assert.Contains("set-stage J-001 ready -By runner -Reason usage-limit -Detail 5-hour limit -Session sess-027", _factory.Calls);
        Assert.DoesNotContain("publish J-001", _factory.Calls);
        Assert.DoesNotContain("set-stage J-002 build -By runner", _factory.Calls);
        Assert.Equal("exit usage-limit", _log[^1]);
    }

    [Fact]
    public void A_429_without_a_session_is_an_escalation()
    {
        _factory.Add("J-001");
        Claude(Stages.Review, """{"type":"result","api_error_status":"429","total_cost_usd":0,"duration_ms":1}""", 1);

        Assert.Equal("drained", Sut.Run());

        Assert.Contains("set-stage J-001 ready -By runner -Reason escalated -Detail claude exited with exit 1; see run-1.jsonl", _factory.Calls);
    }

    [Fact]
    public void Runs_the_gate_itself_when_claude_stops_after_the_approver_approved()
    {
        _factory.Add("J-001");
        _factory.Answers["gate J-001"] = new FactoryResult(0, "", "");
        Claude(Stages.Gate, null, 1);

        Sut.Run();

        Assert.Contains("gate J-001", _factory.Calls);
    }

    [Fact]
    public void A_job_left_in_building_is_escalated_with_claudes_error_and_published()
    {
        _factory.Add("J-001");
        Claude(Stages.Review, """{"type":"result","is_error":true,"result":"Credit balance is too low","total_cost_usd":0,"duration_ms":10}""", 1);

        Sut.Run();

        Assert.Contains("set-stage J-001 ready -By runner -Reason escalated -Detail claude exited with exit 1: Credit balance is too low; see run-1.jsonl", _factory.Calls);
        Assert.Contains("publish J-001", _factory.Calls);

        _factory.Calls.Clear();
        _factory.Add("J-002");
        Claude(Stages.Build, null);
        Sut.Run();
        Assert.Contains("set-stage J-002 ready -By runner -Reason escalated -Detail claude finished but the job is still in Building; see run-1.jsonl", _factory.Calls);
        Assert.Contains("claude J-002 exit=0 result=no", _log);
    }

    [Fact]
    public void A_failed_publish_or_lesson_is_logged()
    {
        _factory.Add("J-001");
        _factory.Answers["publish J-001"] = new FactoryResult(1, "", "factory: publish refused: push failed");
        _factory.Answers["lesson J-001"] = new FactoryResult(1, "", "factory: boom");
        Claude(Stages.Ready);

        Sut.Run();

        Assert.Contains("publish J-001: factory: publish refused: push failed", _log);
        Assert.Contains("lesson J-001: factory: boom", _log);
    }

    [Fact]
    public void An_error_finishing_one_job_is_logged_with_its_stack_the_job_escalated_and_the_queue_moves_on()
    {
        _factory.Add("J-001");
        _factory.Add("J-002");
        _factory.Answers["set-cost J-001"] = new FactoryResult(1, "", "factory: set-cost refused");
        _claude.Run(default!, default!, default!, default!, default!, default!).ReturnsForAnyArgs(c =>
        {
            // J-001 stays building, so the runner's own error escalates it.
            if (c.ArgAt<string>(0) == "J-002") _factory.Stage("J-002", Stages.Ready);
            return new ClaudeRun(0, JsonNode.Parse("""{"total_cost_usd":"n/a","duration_ms":1}""")!.AsObject());
        });

        Assert.Equal("drained", Sut.Run());

        Assert.Contains(_log, l => l.StartsWith("error J-001: set-cost J-001 refused (exit 1)\n", StringComparison.Ordinal) && l.Contains("RunnerLoop", StringComparison.Ordinal));
        Assert.Contains("set-stage J-001 ready -By runner -Reason escalated -Detail runner error: set-cost J-001 refused (exit 1)", _factory.Calls);
        Assert.Contains("stage J-002 ready", _log);
    }

    [Fact]
    public void A_refused_take_or_a_broken_factory_ends_the_drain_as_fatal()
    {
        _factory.Add("J-001");
        _factory.Answers["set-stage J-001 build"] = new FactoryResult(1, "", "refused");
        Assert.Equal("fatal: set-stage J-001 build refused", Sut.Run());

        var broken = Substitute.For<IFactoryCommands>();
        broken.Run(Arg.Any<string[]>()).Returns(_ => throw new InvalidOperationException("the CLI is gone"));
        var lines = new List<string>();
        Assert.Equal("fatal: the CLI is gone", new RunnerLoop(_lock, broken, _claude, _provider, LogTo(lines), Paths, Files, _starter, Microsoft.Extensions.Options.Options.Create(new RunnerOptions())).Run());
        Assert.Equal("exit fatal: the CLI is gone", lines[^1]);
    }

    [Fact]
    public void Unreadable_status_counts_as_no_stage()
    {
        _factory.Add("J-001");
        _factory.Answers["status"] = new FactoryResult(0, "not json", "");
        Claude(Stages.Ready);

        Sut.Run();

        Assert.Contains("stage J-001 ", _log);
    }
    private RunnerLoop One() =>
        new(_lock, _factory, _claude, _provider, LogTo(_log), Paths, Files, _starter, Microsoft.Extensions.Options.Options.Create(new RunnerOptions { One = true }));

    [Fact]
    public void One_takes_a_single_job_starts_the_runner_again_while_the_queue_is_not_empty_and_exits()
    {
        _factory.Add("J-001");
        _factory.Add("J-002");
        Claude(Stages.Ready);
        _starter.Start().Returns("started runner execution r-2");

        Assert.Equal("one", One().Run());

        Assert.Contains("publish J-001", _factory.Calls);
        Assert.DoesNotContain("checkout J-002", _factory.Calls);
        Assert.DoesNotContain("sweep", _factory.Calls);
        _starter.Received(1).Start();
        Assert.Equal(["stage J-001 ready", "started runner execution r-2", "exit one"], _log.TakeLast(3));
    }

    [Fact]
    public void One_with_nothing_left_starts_nothing_and_an_empty_queue_still_drains_and_sweeps()
    {
        _factory.Add("J-001");
        Claude(Stages.Ready);

        Assert.Equal("one", One().Run());
        _starter.DidNotReceive().Start();

        Assert.Equal("drained", One().Run());
        Assert.Equal("sweep", _factory.Calls[^1]);
    }

    [Fact]
    public void The_cost_line_carries_the_result_lines_token_usage()
    {
        _factory.Add("J-001");
        Claude(Stages.Ready, """{"type":"result","total_cost_usd":1.25,"duration_ms":90000,"usage":{"input_tokens":1200,"output_tokens":340}}""");

        Sut.Run();

        Assert.Contains("cost J-001 usd=1.25 ms=90000 in=1200 out=340", _log);
    }
}
