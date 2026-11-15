using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Factory.IntegrationTests;

/// <summary>
/// The GitHub and HCP stub (Factory.TestHost's <see cref="Factory.TestHost.GitHubStubApp"/>), in process on a free localhost
/// port. Repos are bare git repos under <see cref="GitBase"/>, cloned over file:// (<see cref="GitUrl"/>).
/// </summary>
public sealed class GitHubStub : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private Microsoft.AspNetCore.Builder.WebApplication? _app;

    public int Port { get; private set; }
    public string GitBase => _dir["github"];
    public string GitUrl => "file:///" + GitBase.Replace('\\', '/');

    public async ValueTask InitializeAsync()
    {
        Port = Factory.TestHost.StubHost.FreePort();
        _app = Factory.TestHost.StubHost.Build(Port, new Factory.TestHost.GitHubStubApp(GitBase, "test", Port).Handle);
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _dir.Dispose();
    }

    /// <summary>
    /// A .env that points the factory at this stub, signed in as the stub's GitHub App installed on <c>test</c>, whose
    /// secrets go to <c>dir["secrets"]</c> (the caller's FACTORY_SECRET_DIR; FACTORY_LOCAL_DIR keeps the token cache).
    /// </summary>
    public string EnvFile(TempDir dir)
    {
        Factory.TestHost.SimApp.Seed(GitBase, "test", dir["secrets"]);
        var path = dir["github.env"];
        File.WriteAllLines(path, ["GITHUB_OWNER=test", $"GITHUB_API_URL=http://localhost:{Port}", $"FACTORY_GIT_BASE={GitUrl}"]);
        return path;
    }

    /// <summary>The Authorization header of the App's installation tokens (ghs_&lt;installation&gt;_&lt;n&gt;).</summary>
    public const string InstallationAuth = "Bearer ghs_77_";

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