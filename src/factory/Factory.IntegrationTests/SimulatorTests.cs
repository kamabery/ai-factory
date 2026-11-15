using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Factory.IntegrationTests;

/// <summary>
/// The simulator the Playwright suite runs (Factory.Simulator, it was simulate.ps1), as a process. The dashboard tests start
/// it with --reset while the dashboard is live on the same state dir: 3 of 50 runs once failed with "The directory is not
/// empty" (docs/PLAN-PR1-PROOFS.md), so --reset waits out a holder instead of failing the run.
/// </summary>
public sealed class SimulatorTests : IDisposable
{
    private static readonly string Simulator = GitHubStub.RepoFile("artifacts", "bin", "Factory.Simulator", "debug", "Factory.Simulator.dll");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Reset_waits_out_something_holding_the_state_dir_then_runs_the_whole_simulation()
    {
        var state = Directory.CreateDirectory(_dir["sim"]).FullName;
        // A handle shared with nobody: on Windows the folder cannot be deleted until it closes (elsewhere it can be at once).
        var busy = new FileStream(Path.Combine(state, "busy.txt"), FileMode.Create, FileAccess.Write, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(2000); await busy.DisposeAsync(); }, TestContext.Current.CancellationToken);
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { Simulator, "--reset", "--speed", "200", "--state-dir", state }) psi.ArgumentList.Add(a);
        // No .env of its own, so it hosts its own GitHub stub; and no secret store or checkout state of this machine's.
        psi.Environment.Remove("FACTORY_ENV_FILE");
        psi.Environment["FACTORY_SECRET_DIR"] = _dir["secrets"];
        psi.Environment.Remove("FACTORY_WORK");
        psi.Environment.Remove("FACTORY_GATE_CMDS");

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = await p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        await release;

        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {await err}");
        Assert.Contains("simulation complete", output);
        Assert.False(File.Exists(Path.Combine(state, "busy.txt")));
        var jobs = JsonNode.Parse(File.ReadAllText(Path.Combine(state, "board.json")))!["jobs"]!.AsArray();
        Assert.Equal(["done", "ready", "ready", "discarded", "backlog", "ready", "ready", "ready"], jobs.Select(j => (string?)j!["stage"]));
        Assert.Equal("up", (string?)jobs[7]!["deploy"]!["status"]);
    }
}
