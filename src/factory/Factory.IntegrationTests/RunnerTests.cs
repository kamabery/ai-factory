using System.Text.Json.Nodes;
using Factory.Runner;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>The runner's lock on a real disk, and a claude pass through a real pwsh stand-in.</summary>
public sealed class RunnerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private FactoryPaths Paths(string? scripts = null) =>
        new(Options.Create(new FactoryOptions { Home = _dir["state"], Scripts = scripts ?? _dir["scripts"], Templates = _dir["tp"] }), new GitClient(new ProcessRunner()));

    private sealed class LocalState : IStateSettings
    {
        public bool IsAzure => false;
        public string? ContainerUrl => null;
        public long Cap => 0;
        public string? Account => null;
        public string Group => "";
    }

    private RunnerLock Lock() => new(Paths(), new LocalState(), null!, Options.Create(new StateOptions()));

    private string LockFile => Path.Combine(_dir["state"], "runner.lock");

    [Fact]
    public void The_lock_holds_this_process_id_refuses_a_second_taker_and_is_gone_once_released()
    {
        var held = Lock().TryTake();

        Assert.NotNull(held);
        Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(LockFile));
        Assert.Null(Lock().TryTake());
        held!.Dispose();
        Assert.False(File.Exists(LockFile));
        held.Dispose();
    }

    [Fact]
    public void A_lock_left_by_a_dead_process_is_taken_over_and_one_held_by_a_live_process_is_not()
    {
        Directory.CreateDirectory(_dir["state"]);
        File.WriteAllText(LockFile, "999999");
        using (var taken = Lock().TryTake()) Assert.NotNull(taken);

        File.WriteAllText(LockFile, Environment.ProcessId.ToString());
        Assert.Null(Lock().TryTake());
        Assert.True(File.Exists(LockFile));
    }

    [Fact]
    public void A_claude_pass_streams_every_line_to_the_log_as_utf8_with_its_environment_and_returns_the_result()
    {
        var fake = _dir["fake.ps1"];
        File.WriteAllText(fake, """
            '{"type":"system","subtype":"init"}'
            [Console]::Error.WriteLine('warn: from stderr')
            $held = [IO.File]::Open((Join-Path $env:FACTORY_HOME "jobs/$env:FACTORY_JOB/run-1.jsonl"), 'Open', 'Read', 'ReadWrite, Delete')
            '{"type":"assistant","text":"caf' + [char]0xE9 + ' ' + [char]0x2713 + '"}'
            $held.Dispose()
            "key=[$env:ANTHROPIC_API_KEY] resume=[$env:FACTORY_CLAUDE_RESUME] cwd=$((Get-Location).Path)"
            '{"type":"result","total_cost_usd":0.25,"duration_ms":7}'
            exit 1
            """);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-from-the-runner");
        try
        {
            var options = Options.Create(new RunnerOptions { ClaudeCmd = $"& '{fake}'" });
            var log = Path.Combine(_dir["state"], "jobs", "J-001", "run-1.jsonl");
            var wt = Directory.CreateDirectory(_dir["wt"]).FullName;

            var run = new ClaudeRunner(Paths(), new FileStore(), options)
                .Run("J-001", "/factory:factory run J-001", wt, log, new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = null }, "sess-9");

            Assert.Equal((1, 0.25), (run.Code, (double)run.Result!["total_cost_usd"]!));
            var lines = File.ReadAllLines(log);
            Assert.Equal("{\"type\":\"system\",\"subtype\":\"init\"}", lines[0]);
            Assert.Contains("{\"type\":\"assistant\",\"text\":\"café ✓\"}", lines);
            Assert.Contains("warn: from stderr", lines);
            Assert.Contains($"key=[] resume=[sess-9] cwd={wt}", lines);
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir["state"], "headless-settings.json")))!;
            Assert.Equal("5400000", (string?)settings["env"]!["BASH_MAX_TIMEOUT_MS"]);
        }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null); }
    }
}
