using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.IntegrationTests;

/// <summary>
/// The CLI signed in as the GitHub App (contract, "Secrets, init and doctor"), end to end against the GitHub stub: the
/// App's secrets in a FACTORY_SECRET_DIR, no GitHub PAT anywhere, installation tokens for the calls and the user token
/// for creating a repo.
/// </summary>
[Collection(GitHubStubCollection.Name)]
public sealed class CliGitHubAppTests : IDisposable
{
    private readonly GitHubStub _gh;
    private readonly TempDir _dir = new();
    private readonly string _env;

    public CliGitHubAppTests(GitHubStub gh)
    {
        _gh = gh;
        _env = _dir["app.env"];
        File.WriteAllLines(_env, [$"GITHUB_API_URL=http://localhost:{gh.Port}", $"GITHUB_WEB_URL=http://localhost:{gh.Port}", $"FACTORY_GIT_BASE={gh.GitUrl}", "GITHUB_OWNER=test"]);
        var app = new GitHubAppClient(new HttpClient(), new DotEnvFile(_env));
        var reg = app.ConvertManifest("sim-code");
        var store = new FileSecretStore(_dir["secrets"]);
        store.Set(SecretNames.AppId, reg.Id);
        store.Set(SecretNames.AppSlug, reg.Slug);
        store.Set(SecretNames.AppClientId, reg.ClientId);
        store.Set(SecretNames.AppClientSecret, reg.ClientSecret);
        store.Set(SecretNames.AppPrivateKey, reg.PrivateKey);
        AppCredential.Keep(store, app.ExchangeCode(reg.ClientId, reg.ClientSecret, "sim-code", "http://127.0.0.1/callback"));
        File.WriteAllText(Path.Combine(gh.GitBase, "installations.json"), """[{ "id": 5, "account": "test", "selection": "all", "permissions": { "contents": "write" } }]""");
    }

    public void Dispose()
    {
        File.Delete(Path.Combine(_gh.GitBase, "installations.json"));
        _dir.Dispose();
    }

    private Dictionary<string, string?> Env() => new()
    {
        ["FACTORY_HOME"] = _dir["state"], ["FACTORY_ENV_FILE"] = _env, ["FACTORY_SECRET_DIR"] = _dir["secrets"], ["FACTORY_LOCAL_DIR"] = _dir["local"],
    };

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = CliHost.Run(args, Env(), _dir.Path, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private List<JsonObject> Requests(string path) =>
        [.. File.ReadAllLines(Path.Combine(_gh.GitBase, "requests.jsonl")).Select(l => JsonNode.Parse(l)!.AsObject()).Where(r => (string?)r["path"] == path)];

    [Fact]
    public void Lists_the_installations_repos_with_an_installation_token_minted_once_and_cached()
    {
        var repo = "test/app-" + Guid.NewGuid().ToString("N")[..8];
        _gh.NewRemote(repo, _dir);
        var minted = Requests("/app/installations/5/access_tokens").Count;

        var (code, output, err) = Run("list-repos");
        Run("list-repos");

        Assert.Equal((0, ""), (code, err));
        Assert.Contains($"\"{repo}\"", output, StringComparison.Ordinal);
        Assert.Equal(minted + 1, Requests("/app/installations/5/access_tokens").Count);
        Assert.All(Requests("/installation/repositories").TakeLast(2), r => Assert.StartsWith("Bearer ghs_5_", (string)r["auth"]!, StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_dir["local"], "github-token-test.json")));
    }

    [Fact]
    public void Check_target_runs_on_the_App()
    {
        var repo = "test/app-" + Guid.NewGuid().ToString("N")[..8];
        _gh.NewRemote(repo, _dir);

        Assert.Equal((0, "existing\n", ""), Run("check-target", repo));
        Assert.Equal((0, "new\n", ""), Run("check-target", "test/missing-" + Guid.NewGuid().ToString("N")[..8]));
    }

    [Fact]
    public void A_new_repo_is_created_with_the_user_token_which_an_installation_token_could_not()
    {
        var repo = "test/new-" + Guid.NewGuid().ToString("N")[..8];
        using var sp = Composition.Build(new ServiceCollection().AddFactoryCli(new CliEnvironment(Env(), _dir.Path, "\n", _dir["appdata"], Path.GetTempPath(), _dir.Path)));

        Assert.Null(sp.GetRequiredService<IGitHub>().CreateRepo(repo));

        Assert.True(Directory.Exists(Path.Combine(_gh.GitBase, repo + ".git")));
        Assert.StartsWith("Bearer ghu_", (string)Requests("/user/repos").Last()["auth"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_owner_the_App_is_not_installed_on_is_refused_in_one_line()
    {
        File.WriteAllText(Path.Combine(_gh.GitBase, "installations.json"), """[{ "id": 6, "account": "someone-else", "selection": "all" }]""");

        var (code, _, err) = Run("list-repos");

        Assert.Equal((1, "factory: list-repos refused: GitHub 0: the GitHub App is not installed on test; install it (factory init), or check GITHUB_OWNER\n"), (code, err));
    }
}
