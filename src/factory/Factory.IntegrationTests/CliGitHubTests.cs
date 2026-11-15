using System.Text.Json.Nodes;
using Factory.Cli;

namespace Factory.IntegrationTests;

/// <summary>The idea and target commands end to end, in process, against github-stub.ps1.</summary>
[Collection(GitHubStubCollection.Name)]
public sealed class CliGitHubTests(GitHubStub gh) : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _repo = "test/repo-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _new = "test/new-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose() => _dir.Dispose();

    private string Home => _dir["state"];

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = Home, ["FACTORY_ENV_FILE"] = gh.EnvFile(_dir), ["FACTORY_SECRET_DIR"] = _dir["secrets"], ["FACTORY_LOCAL_DIR"] = _dir["local"],
            ["FACTORY_SCRIPTS"] = GitHubStub.RepoFile("factory", "scripts"), ["FACTORY_TEMPLATES"] = "TP",
        };
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = CliHost.Run(args, env, _dir.Path, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string GitOut(string dir, params string[] args) =>
        new Factory.Store.GitClient(new Factory.Store.ProcessRunner()).Run(dir, args).Out;

    private JsonObject Board => JsonNode.Parse(File.ReadAllText(Path.Combine(Home, "board.json")))!.AsObject();

    [Fact]
    public void CheckTarget_asks_GitHub_with_the_Apps_installation_token_and_writes_no_board()
    {
        gh.NewRemote(_repo, _dir);
        gh.NewEmpty(_new);

        Assert.Equal((0, "existing\n", ""), Run("check-target", _repo));
        Assert.Equal((0, "new\n", ""), Run("check-target", _new));
        Assert.Equal((0, "new\n", ""), Run("check-target", "test/missing-" + Guid.NewGuid().ToString("N")[..8]));
        Assert.False(File.Exists(Path.Combine(Home, "board.json")));
        Assert.All(gh.Requests(_repo), r => Assert.StartsWith(GitHubStub.InstallationAuth, (string?)r["auth"], StringComparison.Ordinal));
    }

    [Fact]
    public void ListRepos_prints_the_users_repos_newest_push_first()
    {
        gh.NewRemote(_repo, _dir);
        Directory.SetLastWriteTime(Path.Combine(gh.GitBase, _repo + ".git"), DateTime.Now.AddDays(-30));
        gh.NewRemote(_new, _dir);

        var (code, output, _) = Run("list-repos");

        var repos = JsonNode.Parse(output)!.AsArray().Select(r => (string?)r).ToList();
        Assert.Equal(0, code);
        Assert.True(repos.IndexOf(_new) >= 0 && repos.IndexOf(_new) < repos.IndexOf(_repo));
    }

    [Fact]
    public void NewIdea_on_a_new_repo_adds_a_bootstrap_job_from_the_real_template_and_AddSpec_a_feature()
    {
        var (code, output, err) = Run("new-idea", "a todo app", "-Target", _new, "-Scaffold", """{"name":"todo","resource":"Todo","db":"sqlite","ui":"react","events":false}""");

        Assert.Equal((0, "I-001 J-001\n", ""), (code, output, err));
        var spec = File.ReadAllText(Path.Combine(Home, "jobs", "J-001", "spec.md"));
        Assert.StartsWith("# Spec: J-001 — Bootstrap todo from template-projects", spec);
        Assert.Contains($"- Repo: {_new}", spec);
        Assert.Contains("`aspire-addon add ui-react`", spec);
        Assert.Contains("TP/.claude/skills/boot-strap-app/SKILL.md", spec);
        Assert.DoesNotContain("{{", spec);
        Assert.Equal("waiting", (string?)Board["ideas"]![0]!["status"]);

        var file = _dir["feature.md"];
        File.WriteAllText(file, $"# Spec: Search\n\n## Target\n- Repo: {_new}\n");
        Assert.Equal((0, "J-002\n", ""), Run("add-spec", "I-001", file));
        Assert.Equal(["J-001", "J-002"], Board["ideas"]![0]!["jobs"]!.AsArray().Select(j => (string?)j));
    }

    [Fact]
    public void CloneTarget_replaces_the_folder_with_a_shallow_clone_and_keeps_the_PAT_out_of_git_config()
    {
        gh.NewRemote(_repo, _dir);
        var dir = _dir["clone"];
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stale.txt"), "old");

        Assert.Equal((0, dir + "\n", ""), Run("clone-target", _repo, dir));
        Assert.False(File.Exists(Path.Combine(dir, "stale.txt")));
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(dir, "README.md")).TrimEnd());
        Assert.Equal("true", GitOut(dir, "rev-parse", "--is-shallow-repository"));
        Assert.DoesNotContain("ghs_", File.ReadAllText(Path.Combine(dir, ".git", "config")));
    }

    [Fact]
    public void A_refused_GitHub_call_is_the_commands_refusal_and_a_GitHub_PAT_is_not_a_fallback()
    {
        var bad = _dir["bad.env"];
        File.WriteAllLines(bad, ["GITHUB_TOKEN=ghp_still_here", $"GITHUB_API_URL=http://localhost:{gh.Port}"]);
        var env = new Dictionary<string, string?> { ["FACTORY_HOME"] = Home, ["FACTORY_ENV_FILE"] = bad, ["FACTORY_SECRET_DIR"] = _dir["no-app"], ["FACTORY_LOCAL_DIR"] = _dir["local"] };
        var stderr = new StringWriter { NewLine = "\n" };

        var code = CliHost.Run(["check-target", _repo], env, _dir.Path, new StringWriter(), stderr);

        Assert.Equal((1, "factory: check-target refused: GitHub 0: no GitHub App is set up; run factory init\n"), (code, stderr.ToString()));
    }
}
