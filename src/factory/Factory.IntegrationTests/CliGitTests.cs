using System.Text.Json.Nodes;
using Factory.Cli;

namespace Factory.IntegrationTests;

/// <summary>checkout, gate-check, gate, publish and merge end to end, in process, against github-stub.ps1 and real git.</summary>
[Collection(GitHubStubCollection.Name)]
public sealed class CliGitTests(GitHubStub gh) : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _repo = "test/git-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose() => _dir.Dispose();

    private string Home => _dir["state"];
    private string Wt => Path.Combine(_dir["work"], "jobs", "J-001");

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = Home, ["FACTORY_ENV_FILE"] = gh.EnvFile(_dir), ["FACTORY_WORK"] = _dir["work"], ["FACTORY_SECRET_DIR"] = _dir["secrets"],
            ["FACTORY_SCRIPTS"] = GitHubStub.RepoFile("factory", "scripts"),
        };
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = CliHost.Run(args, env, _dir.Path, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private JsonObject Job => JsonNode.Parse(File.ReadAllText(Path.Combine(Home, "board.json")))!["jobs"]![0]!.AsObject();

    private static string Git(string dir, params string[] args) => new Factory.Store.GitClient(new Factory.Store.ProcessRunner()).Run(dir, args).Out;

    /// <summary>A job in <paramref name="stage"/> on the stub's repo, its spec's gate one pwsh command.</summary>
    private void Board(string stage, string gate = "exit 0")
    {
        Directory.CreateDirectory(Path.Combine(Home, "jobs", "J-001"));
        File.WriteAllText(Path.Combine(Home, "jobs", "J-001", "spec.md"), $"# Spec: Ship it\n\n## Target\n- Repo: {_repo}\n\n## Gate\n- build: `{gate}`\n");
        File.WriteAllText(Path.Combine(Home, "board.json"), new JsonObject
        {
            ["version"] = 2, ["ideas"] = new JsonArray(),
            ["jobs"] = new JsonArray(new JsonObject
            {
                ["id"] = "J-001", ["idea"] = null, ["kind"] = "feature", ["title"] = "Ship it", ["stage"] = stage, ["round"] = 1, ["target"] = _repo,
                ["pr"] = null, ["gate"] = null, ["flag"] = null, ["merged"] = null, ["history"] = new JsonArray(),
            }),
        }.ToJsonString());
    }

    private void SetStage(string stage)
    {
        var board = JsonNode.Parse(File.ReadAllText(Path.Combine(Home, "board.json")))!;
        board["jobs"]![0]!["stage"] = stage;
        File.WriteAllText(Path.Combine(Home, "board.json"), board.ToJsonString());
    }

    [Fact]
    public void Checkout_gate_publish_and_merge_land_the_work_on_main_and_leave_no_PAT_on_disk()
    {
        gh.NewRemote(_repo, _dir);
        Board("build");

        Assert.Equal((0, Wt + "\n", ""), Run("checkout", "J-001"));
        Assert.Equal("factory/J-001", Git(Wt, "branch", "--show-current"));
        Assert.Equal(Git(Wt, "rev-parse", "origin/main"), Git(Wt, "rev-parse", "refs/remotes/factory/base"));
        Assert.Equal(Git(Wt, "rev-parse", "HEAD"), (string?)Job["roundStart"]);

        File.WriteAllText(Path.Combine(Wt, "feature.txt"), "work\n");
        Git(Wt, "add", "-A");
        Git(Wt, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "work");
        var head = Git(Wt, "rev-parse", "HEAD");

        var check = Run("gate-check", "J-001");
        Assert.Equal(0, check.Code);
        Assert.Contains("exit 0\n", check.Out);
        Assert.Contains($"exit 0 (cached at {head[..7]})", Run("gate-check", "J-001").Out);

        SetStage("gate");
        Assert.Equal((0, "", ""), Run("gate", "J-001"));
        Assert.Equal(("ready", head), ((string?)Job["stage"], (string?)Job["gate"]!["sha"]));
        Assert.Contains("## build: exit 0", File.ReadAllText(Path.Combine(Home, "jobs", "J-001", "gate.log")));

        var (code, url, _) = Run("publish", "J-001");
        Assert.Equal(0, code);
        Assert.EndsWith("/pull/1\n", url);

        Assert.Equal((0, "", ""), Run("merge", "J-001"));
        var bare = Path.Combine(gh.GitBase, _repo + ".git");
        Assert.Equal("work", Git(bare, "show", "main:feature.txt"));
        Assert.Equal(("done", Git(bare, "rev-parse", "main")), ((string?)Job["stage"], (string?)Job["merged"]!["sha"]));
        Assert.Equal(["feature.txt"], Job["merged"]!["added"]!.AsArray().Select(p => (string?)p));
        Assert.False(Directory.Exists(Wt));
        Assert.Empty(Git(bare, "branch", "--list", "factory/J-001"));
        Assert.All(gh.Requests(_repo), r => Assert.Equal($"Bearer {GitHubStub.Token}", (string?)r["auth"]));
        foreach (var f in Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(GitHubStub.Token, File.ReadAllText(f));
    }

    [Fact]
    public void A_red_gate_lands_in_ready_flagged_and_merge_refuses_it()
    {
        gh.NewRemote(_repo, _dir);
        Board("build", "exit 4");
        Run("checkout", "J-001");
        File.WriteAllText(Path.Combine(Wt, "feature.txt"), "work\n");
        Git(Wt, "add", "-A");
        Git(Wt, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "work");
        SetStage("gate");

        Assert.Equal((1, "", ""), Run("gate", "J-001"));
        Assert.Equal(("ready", "gate-failed", 4), ((string?)Job["stage"], (string?)Job["flag"]!["reason"], (int?)Job["gate"]!["build"]));

        Assert.Equal((1, "", "factory: merge refused: gate red\n"), Run("merge", "J-001"));
        Assert.Null(Job["merged"]);
    }
}
