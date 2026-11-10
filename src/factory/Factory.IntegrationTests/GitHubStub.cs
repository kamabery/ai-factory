using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Factory.IntegrationTests;

/// <summary>
/// factory/tests/fixtures/github-stub.ps1, the GitHub REST stand-in the Pester suites use, on a free localhost port.
/// Repos are bare git repos under <see cref="GitBase"/>, cloned over file:// (<see cref="GitUrl"/>).
/// </summary>
public sealed class GitHubStub : IAsyncLifetime
{
    public const string Token = "ghp_test_token";
    private static readonly HttpClient Http = new();
    private readonly TempDir _dir = new();
    private Process? _process;

    public int Port { get; private set; }
    public string GitBase => _dir["github"];
    public string GitUrl => "file:///" + GitBase.Replace('\\', '/');

    public async ValueTask InitializeAsync()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var psi = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-File", RepoFile("factory", "tests", "fixtures", "github-stub.ps1"), "-Port", $"{Port}", "-GitBase", GitBase, "-Login", "test" })
            psi.ArgumentList.Add(a);
        _process = Process.Start(psi)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { (await Http.GetAsync($"http://localhost:{Port}/")).EnsureSuccessStatusCode(); return; }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline) { await Task.Delay(200); }
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _process?.Kill(true); } catch (InvalidOperationException) { }
        _process?.Dispose();
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A .env that points the factory at this stub, signed in with <see cref="Token"/>.</summary>
    public string EnvFile(TempDir dir)
    {
        var path = dir["github.env"];
        File.WriteAllLines(path, [$"GITHUB_TOKEN={Token}", $"GITHUB_API_URL=http://localhost:{Port}", $"FACTORY_GIT_BASE={GitUrl}"]);
        return path;
    }

    /// <summary>A repo with one commit on main (README.md: fixture), as the stub serves it.</summary>
    public void NewRemote(string repo, TempDir scratch)
    {
        var seed = new GitRepo(scratch["seed-" + Guid.NewGuid().ToString("N")[..8]]);
        seed.Commit("init", ("README.md", "fixture\n"));
        var bare = Path.Combine(GitBase, repo + ".git");
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        Git(scratch.Path, "clone", "-q", "--bare", seed.Path, bare);
    }

    /// <summary>An empty bare repo: on GitHub, a repo with no main.</summary>
    public void NewEmpty(string repo) => Git(GitBase, "init", "-q", "--bare", "-b", "main", Path.Combine(GitBase, repo + ".git"));

    /// <summary>The requests the stub logged for this repo's paths: { method, path, auth, body }.</summary>
    public IReadOnlyList<JsonObject> Requests(string repo) =>
        [.. File.ReadAllLines(Path.Combine(GitBase, "requests.jsonl")).Select(l => JsonNode.Parse(l)!.AsObject())
            .Where(r => ((string?)r["path"])!.StartsWith($"/repos/{repo}", StringComparison.Ordinal))];

    /// <summary>A file of this repository, found above the test assembly.</summary>
    public static string RepoFile(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine([d.FullName, .. parts]);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        throw new FileNotFoundException(string.Join('/', parts) + " not found above " + AppContext.BaseDirectory);
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr.Result}");
    }
}