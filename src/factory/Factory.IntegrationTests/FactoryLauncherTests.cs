using System.Diagnostics;
using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>
/// The detached <c>dotnet &lt;factory.dll&gt; &lt;command&gt;</c> (contract, "Implementations"), with the real CLI built beside
/// this test. The test holds the board lock, so the child's <c>event</c> waits on it: whatever happens while it is held
/// happened while the child was still running.
/// </summary>
public sealed class FactoryLauncherTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Starts_the_CLI_with_the_arguments_and_returns_without_waiting_for_it()
    {
        var home = Directory.CreateDirectory(_dir["home"]).FullName;
        string started;
        var release = HoldBoard(home);
        try
        {
            started = await Parent(home, TimeSpan.FromSeconds(60));
            Assert.False(File.Exists(Path.Combine(home, "events.jsonl")), "the child ran while the board was held");
        }
        finally { release(); }

        Assert.True(int.Parse(started) < 2000, $"StartDetached took {started} ms: it waited for the child");
        Assert.Contains("\"detail\":\"launched\"", await Events(home));
    }

    [Fact]
    public async Task The_child_holds_none_of_its_parents_output_so_a_caller_reading_it_is_not_kept_waiting()
    {
        // The runner reads `merge`'s output to its end. A detached destroy that inherited that pipe would hold it open
        // for as long as the child runs, here for as long as the test holds the board.
        var home = Directory.CreateDirectory(_dir["home"]).FullName;
        var watch = Stopwatch.StartNew();
        var release = HoldBoard(home);
        try
        {
            await Parent(home, TimeSpan.FromSeconds(60));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"reading the parent's output took {watch.Elapsed}: the child held it");
            Assert.False(File.Exists(Path.Combine(home, "events.jsonl")));
        }
        finally { release(); }

        Assert.Contains("\"detail\":\"launched\"", await Events(home));
    }

    /// <summary>
    /// A parent process (pwsh, loading this build's Factory.Store) that calls StartDetached for <c>event</c> on
    /// <paramref name="home"/>, prints how long the call took, and exits; returns what it printed once its output closes.
    /// </summary>
    private async Task<string> Parent(string home, TimeSpan timeout)
    {
        var bin = AppContext.BaseDirectory;
        var envFile = _dir["empty.env"];
        File.WriteAllText(envFile, "");
        // Options comes from the shared ASP.NET Core framework (the server's tests reference it), not this bin.
        var options = typeof(Options).Assembly.Location;
        var script = string.Join("; ",
            $"Add-Type -Path '{options}'",
            $"foreach ($d in 'Factory.Core', 'Factory.Store') {{ Add-Type -Path (Join-Path '{bin}' \"$d.dll\") }}",
            $"$o = [Microsoft.Extensions.Options.Options]::Create([Factory.Store.FactoryOptions]@{{ BaseDirectory = '{bin}' }})",
            "$paths = [Factory.Store.FactoryPaths]::new($o, [Factory.Store.GitClient]::new([Factory.Store.ProcessRunner]::new()))",
            "$w = [Diagnostics.Stopwatch]::StartNew()",
            "[Factory.Store.FactoryLauncher]::new($paths).StartDetached([string[]]@('event', '-Kind', 'note', '-Detail', 'launched'))",
            "$w.ElapsedMilliseconds");
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-Command", script }) psi.ArgumentList.Add(a);
        // The child inherits these: a state dir of its own, and no .env from whatever checkout the test runs in.
        psi.Environment["FACTORY_HOME"] = home;
        psi.Environment["FACTORY_ENV_FILE"] = envFile;
        using var cts = new CancellationTokenSource(timeout);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
        await p.WaitForExitAsync(cts.Token);
        Assert.Equal("", (await stderr).Trim());
        return output.Trim();
    }

    private static async Task<string> Events(string home)
    {
        var path = Path.Combine(home, "events.jsonl");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(path) && DateTime.UtcNow < deadline) await Task.Delay(200, TestContext.Current.CancellationToken);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    /// <summary>Holds the board lock for <paramref name="home"/> on a thread of its own; returns what releases it.</summary>
    private static Action HoldBoard(string home)
    {
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            using var m = new BoardMutex(new FactoryPaths(Options.Create(new FactoryOptions { Home = home }), new GitClient(new ProcessRunner())));
            m.Enter(30);
            held.Set();
            release.Wait();
            m.Exit();
        });
        t.Start();
        held.Wait();
        return () => { release.Set(); t.Join(); };
    }
}
