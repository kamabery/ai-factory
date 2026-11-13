using System.Text.Json.Nodes;
using Factory.Cli;

namespace Factory.IntegrationTests;

/// <summary>
/// deploy, deploy-log, qa-login and destroy end to end, in process, against github-stub.ps1 (which serves HCP's API too),
/// real git, and gh, az and the sign-in smoke as small pwsh scripts.
/// </summary>
[Collection(GitHubStubCollection.Name)]
public sealed class CliDeployTests(GitHubStub gh) : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _repo = "test/deploy-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose() => _dir.Dispose();

    private string Home => _dir["state"];
    private string Wt => Path.Combine(_dir["work"], "jobs", "J-001");
    private string Name => _repo.Split('/')[1];

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var envFile = _dir["deploy.env"];
        File.WriteAllLines(envFile, File.ReadAllLines(gh.EnvFile(_dir)).Concat([
            "HCP_ORG=test-org", "HCP_TOKEN=hcp-test", "HCP_TEAM_TOKEN=team", "HCP_AZURE_VARSET=varset-azure", "AZURE_GH_CLIENT_ID=gh-app",
            "AZURE_TENANT_ID=tenant", "AZURE_SUBSCRIPTION_ID=sub", $"HCP_API_URL=http://localhost:{gh.Port}"]));
        var tool = Script("tool.ps1", "$a = @($args); if ($a -contains 'list') { '[]' } else { '{}' }");
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = Home, ["FACTORY_ENV_FILE"] = envFile, ["FACTORY_WORK"] = _dir["work"], ["FACTORY_SCRIPTS"] = GitHubStub.RepoFile("factory", "scripts"),
            ["FACTORY_GH_CMD"] = tool, ["FACTORY_AZ_CMD"] = tool, ["FACTORY_DEPLOY_POLL_SECONDS"] = "0", ["FACTORY_SMOKE_SECONDS"] = "0",
            // The password arrives on stdin; the stub proves it by printing it, and the log must show it masked.
            ["FACTORY_SIGNIN_CMD"] = Script("signin.ps1", "$pw = [Console]::In.ReadToEnd().Trim(); \"step: typed $pw\"; 'step: signed in: Sign out is showing'"),
        };
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = CliHost.Run(args, env, _dir.Path, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string Script(string name, string text)
    {
        var p = _dir[name];
        if (!File.Exists(p)) File.WriteAllText(p, text);
        return p;
    }

    private JsonObject Job => JsonNode.Parse(File.ReadAllText(Path.Combine(Home, "board.json")))!["jobs"]![0]!.AsObject();

    private JsonObject State => JsonNode.Parse(File.ReadAllText(Path.Combine(gh.GitBase, "deploy-state.json")))!.AsObject();

    private static string Git(string dir, params string[] args) => new Factory.Store.GitClient(new Factory.Store.ProcessRunner()).Run(dir, args).Out;

    /// <summary>A job in deploy, its work dir cloned by checkout with one commit on the branch (plus <paramref name="file"/>).</summary>
    private void InDeploy(string? file = null)
    {
        gh.NewRemote(_repo, _dir);
        Directory.CreateDirectory(Path.Combine(Home, "jobs", "J-001"));
        File.WriteAllText(Path.Combine(Home, "jobs", "J-001", "spec.md"), $"# Spec: Ship it\n\n## Target\n- Repo: {_repo}\n");
        File.WriteAllText(Path.Combine(Home, "board.json"), new JsonObject
        {
            ["version"] = 2, ["ideas"] = new JsonArray(),
            ["jobs"] = new JsonArray(new JsonObject
            {
                ["id"] = "J-001", ["kind"] = "feature", ["title"] = "Ship it", ["stage"] = "deploy", ["round"] = 1, ["target"] = _repo,
                ["flag"] = null, ["history"] = new JsonArray(),
            }),
        }.ToJsonString());
        Assert.Equal(0, Run("checkout", "J-001").Code);
        File.WriteAllText(Path.Combine(Wt, file ?? "feature.txt"), "work\n");
        Git(Wt, "add", "-A");
        Git(Wt, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "work");
    }

    [Fact]
    public void Deploy_applies_the_preview_smoke_tests_it_moves_the_job_on_and_destroy_takes_it_down()
    {
        InDeploy();

        var (code, output, err) = Run("deploy", "J-001");

        Assert.True(code == 0, err);
        Assert.Contains($"frontend: http://localhost:{gh.Port}/preview/{_repo}/j001/web\n", output);
        var d = Job["deploy"]!;
        Assert.Equal(("approve", "up", 1, Git(Wt, "rev-parse", "HEAD")), ((string?)Job["stage"], (string?)d["status"], (int)d["attempts"]!, (string?)d["sha"]));
        var signin = File.ReadAllText(Path.Combine(Home, "jobs", "J-001", "signin", "smoke-1.log"));
        Assert.Contains("step: typed ***", signin);
        Assert.DoesNotContain("pw-j001", signin);
        Assert.Contains("/j001/web/api/health answered 200", File.ReadAllText(Path.Combine(Home, "jobs", "J-001", "smoke-1.log")));

        Assert.Equal((0, $"user: qa-j001@{Name}.test\npassword: pw-j001\n", ""), Run("qa-login", "J-001"));

        Assert.Equal((0, "down\n", ""), Run("destroy", "J-001"));
        Assert.Equal("down", (string?)Job["deploy"]!["status"]);
        Assert.True((bool)State["workspaces"]![(string)d["workspaceId"]!]!["deleted"]!);
        Assert.Equal((0, "no preview for J-001\n", ""), Run("destroy", "J-001"));
    }

    [Fact]
    public void A_failed_run_is_a_failed_attempt_whose_log_deploy_log_prints()
    {
        InDeploy("deploy-fail.txt");

        var (code, _, err) = Run("deploy", "J-001");

        Assert.Equal(1, code);
        Assert.StartsWith("factory: deploy attempt 1 failed: Error: work; see deploy-1.log", err);
        Assert.Equal(("deploy", "failed"), ((string?)Job["stage"], (string?)Job["deploy"]!["status"]));
        var log = Run("deploy-log", "J-001").Out;
        Assert.StartsWith("## GitHub\nrun https://github.test/", log);
        Assert.Contains("## HCP\nrun run-", log);
    }
}
