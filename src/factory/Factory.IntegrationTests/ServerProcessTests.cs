using System.Diagnostics;
using System.Text.RegularExpressions;
using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>
/// The dashboard server as <c>factory serve</c> starts it (contract, "Implementations"): its own process, built beside these
/// tests, on a state dir of its own.
/// </summary>
public sealed partial class ServerProcessTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        _dir.Dispose();
    }

    private static string ServerDll =>
        new FactoryPaths(Options.Create(new FactoryOptions { BaseDirectory = AppContext.BaseDirectory }), new GitClient(new ProcessRunner())).ServerDll;

    private Process Server(params string[] args) => ServerOn("state", args);

    private Process ServerOn(string state, params string[] args)
    {
        var env = _dir["empty.env"];
        File.WriteAllText(env, "");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in (string[])[ServerDll, "--state-dir", _dir[state], "--env-file", env, .. args]) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    /// <summary>The URL from the server's start line.</summary>
    private static async Task<string> Url(Process p)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (await p.StandardOutput.ReadLineAsync(cts.Token) is { } line)
            if (StartLine().Match(line) is { Success: true } m) return m.Groups[1].Value;
        throw new InvalidOperationException("the server ended without its start line: " + await p.StandardError.ReadToEndAsync(cts.Token));
    }

    [Fact]
    public async Task Without_a_port_it_takes_a_free_one_and_prints_it_so_two_dashboards_never_collide()
    {
        var one = await Url(ServerOn("one"));
        var two = await Url(ServerOn("two"));

        using var http = new HttpClient();
        Assert.NotEqual(one, two);
        foreach (var url in new[] { one, two })
            Assert.True((await http.GetAsync(url, TestContext.Current.CancellationToken)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Requiring_sign_in_it_refuses_a_request_without_Easy_Auths_principal_and_serves_one_with_it()
    {
        var url = await Url(Server("--require-signin"));
        using var http = new HttpClient();

        var bare = await http.GetAsync(url, TestContext.Current.CancellationToken);
        using var signed = new HttpRequestMessage(HttpMethod.Get, url);
        signed.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "00000000-0000-0000-0000-000000000001");
        var ok = await http.SendAsync(signed, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, bare.StatusCode);
        Assert.Equal("""{"ok":false,"message":"sign-in required"}""", await bare.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(ok.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_second_dashboard_on_the_same_state_is_refused_naming_the_first()
    {
        var first = Server();
        var url = await Url(first);

        var second = Server();
        var err = await second.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await second.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, second.ExitCode);
        Assert.Equal($"factory serve refused: a dashboard (PID {first.Id}) already serves {Path.GetFullPath(_dir["state"])} at {url}", err.Trim());
    }

    [Fact]
    public async Task It_stops_when_its_parent_is_gone()
    {
        // A parent that lives until its stdin closes.
        var parent = Process.Start(new ProcessStartInfo("git", "hash-object --stdin") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        _started.Add(parent);
        var server = Server("--parent-pid", $"{parent.Id}");
        await Url(server);
        Assert.False(server.HasExited);

        parent.StandardInput.Close();
        await parent.WaitForExitAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.WaitForExitAsync(cts.Token);
        Assert.True(server.HasExited);
    }

    [GeneratedRegex(@"^factory dashboard on (http://localhost:\d+/) ")] private static partial Regex StartLine();
}
