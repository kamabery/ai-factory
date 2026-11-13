using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>The deploy path's own processes and probes, for real: tool overrides, stdin and a child's environment, the smoke GET, canary.lock.</summary>
public sealed class DeployToolsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>The real paths, with factory/scripts and the home in this test's folder.</summary>
    private FactoryPaths Paths(string? home = null) => new(Options.Create(new FactoryOptions { Home = home, Scripts = _dir["scripts"], EnvFile = _dir["none.env"] }),
        new GitClient(new ProcessRunner()));

    private string Script(string name, string text)
    {
        var p = _dir[name];
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void A_gh_override_gets_the_arguments_stdin_and_a_variable_set_for_it_alone()
    {
        var stub = Script("gh.ps1", "\"args: $($args -join ',')\"; \"stdin: $([Console]::In.ReadToEnd())\"; \"token: $env:GH_TOKEN\"; [Console]::Error.WriteLine('warn'); exit 4");
        var tools = new ToolRunner(new ProcessRunner(), Paths(), Options.Create(new DeployOptions { GhCmd = stub }));

        var r = tools.Gh(["secret", "set", "a b"], "team-token", new Dictionary<string, string?> { ["GH_TOKEN"] = "pat" });

        Assert.Equal(new ToolResult(4, "args: secret,set,a b\nstdin: team-token\ntoken: pat\nwarn"), r with { Out = r.Out.Replace("\r\n", "\n") });
        Assert.Null(Environment.GetEnvironmentVariable("GH_TOKEN"));
    }

    [Fact]
    public void A_variable_set_to_null_is_removed_for_the_child()
    {
        Environment.SetEnvironmentVariable("FACTORY_IT_PROBE", "inherited");
        try
        {
            var r = new ProcessRunner().Run("pwsh", ["-NoProfile", "-Command", "\"[$env:FACTORY_IT_PROBE]\""], "", null,
                new Dictionary<string, string?> { ["FACTORY_IT_PROBE"] = null });
            Assert.Equal("[]", r.StdOut.Trim());
        }
        finally { Environment.SetEnvironmentVariable("FACTORY_IT_PROBE", null); }
    }

    [Fact]
    public void A_pwsh_scaffold_command_runs_in_its_directory()
    {
        var dir = Directory.CreateDirectory(_dir["scaffold"]).FullName;
        var tools = new ToolRunner(new ProcessRunner(), Paths(), Options.Create(new DeployOptions()));

        var r = tools.Pwsh(dir, ["-Command", "Set-Content README.md 'canary scaffold'; (Get-Location).Path"]);

        Assert.Equal(0, r.Code);
        Assert.Equal("canary scaffold", File.ReadAllText(Path.Combine(dir, "README.md")).Trim());
    }

    [Fact]
    public async Task The_smoke_GET_reads_status_body_and_Server_and_a_refused_connection_is_status_0()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var http = new HttpListener();
        http.Prefixes.Add($"http://localhost:{port}/");
        http.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            ctx.Response.StatusCode = 503;
            ctx.Response.Headers["Server"] = "envoy";
            var b = "Unhealthy"u8.ToArray();
            await ctx.Response.OutputStream.WriteAsync(b);
            ctx.Response.Close();
        }, TestContext.Current.CancellationToken);
        var probe = new HttpProbe(new HttpClient());

        var r = probe.Get($"http://localhost:{port}/api/health", TimeSpan.FromSeconds(10));
        await serve;

        Assert.Equal((503, "Unhealthy"), (r.Status, r.Body));
        Assert.Contains("envoy", r.Server);
        http.Stop();
        var refused = probe.Get($"http://localhost:{port}/api/health", TimeSpan.FromSeconds(10));
        Assert.Equal((0, "-"), (refused.Status, refused.Server));
        Assert.NotEmpty(refused.Body);
    }

    [Fact]
    public void Canary_lock_holds_this_PID_refuses_a_second_taker_and_takes_over_one_whose_process_is_gone()
    {
        var home = Directory.CreateDirectory(_dir["home"]).FullName;
        var paths = Paths(home);
        var state = Options.Create(new StateOptions());
        var settings = new StateSettings(state, paths, new DotEnv(paths, new FileStore()));
        var sut = new CanaryLock(paths, settings, new BlobClient(new HttpClient(), settings,
            new StateToken(state, new FileStore(), new ProcessRunner(), new HttpClient(), TimeProvider.System), TimeProvider.System), state);
        var file = Path.Combine(home, "canary.lock");

        using (var held = sut.TryTake(out var other))
        {
            Assert.NotNull(held);
            Assert.False(other);
            Assert.Equal($"{Environment.ProcessId}", File.ReadAllText(file));
            Assert.Null(sut.TryTake(out other));
            Assert.False(other);
        }
        Assert.False(File.Exists(file));

        using var gone = Process.Start(new ProcessStartInfo("pwsh", "-NoProfile -Command exit") { UseShellExecute = false })!;
        gone.WaitForExit();
        File.WriteAllText(file, $"{gone.Id}");
        using var taken = sut.TryTake(out _);
        Assert.NotNull(taken);
    }
}
