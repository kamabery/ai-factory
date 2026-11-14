using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Store;
using Factory.TestHost;

namespace Factory.IntegrationTests;

/// <summary>
/// factory doctor end to end on the GitHub stub, and the Foundry probe against a stand-in endpoint (contract, "Secrets,
/// init and doctor"). The tools and llm lines depend on this machine's own claude login, so only the others are asserted.
/// </summary>
[Collection(GitHubStubCollection.Name)]
public sealed class DoctorTests(GitHubStub gh) : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        if (Directory.Exists(gh.GitBase)) File.Delete(Path.Combine(gh.GitBase, "installations.json"));
        _dir.Dispose();
    }

    private (int Code, string Out) Doctor(Dictionary<string, string?> env)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var code = CliHost.Run(["doctor"], env, _dir.Path, stdout, new StringWriter());
        return (code, stdout.ToString());
    }

    [Fact]
    public void Reports_the_App_the_store_and_env_and_fails_on_an_installation_that_lacks_a_permission()
    {
        var envFile = _dir["app.env"];
        File.WriteAllLines(envFile, [$"GITHUB_API_URL=http://localhost:{gh.Port}", $"GITHUB_WEB_URL=http://localhost:{gh.Port}", "GITHUB_OWNER=test", "HCP_TOKEN=in-env"]);
        var app = new GitHubAppClient(new HttpClient(), new DotEnvFile(envFile));
        var reg = app.ConvertManifest("sim-code");
        var store = new FileSecretStore(_dir["secrets"]);
        foreach (var (k, v) in new[] { (SecretNames.AppId, reg.Id), (SecretNames.AppSlug, reg.Slug), (SecretNames.AppClientId, reg.ClientId), (SecretNames.AppClientSecret, reg.ClientSecret), (SecretNames.AppPrivateKey, reg.PrivateKey) })
            store.Set(k, v);
        AppCredential.Keep(store, app.ExchangeCode(reg.ClientId, reg.ClientSecret, "sim-code", "x"));
        var all = new JsonObject();
        foreach (var p in JsonNode.Parse(File.ReadAllText(GitHubStub.RepoFile("factory", "github-app.json")))!["default_permissions"]!.AsObject()) all[p.Key] = p.Value!.DeepClone();
        File.WriteAllText(Path.Combine(gh.GitBase, "installations.json"), new JsonArray(new JsonObject { ["id"] = 9, ["account"] = "test", ["selection"] = "all", ["permissions"] = all }).ToJsonString());
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = _dir["state"], ["FACTORY_ENV_FILE"] = envFile, ["FACTORY_SECRET_DIR"] = _dir["secrets"], ["FACTORY_LOCAL_DIR"] = _dir["local"],
            ["FACTORY_SCRIPTS"] = GitHubStub.RepoFile("factory", "scripts"), ["HCP_API_URL"] = null,
        };

        var (_, output) = Doctor(env);

        var lines = output.Split('\n');
        Assert.Contains($"ok    secret-store: the folder {_dir["secrets"]}", lines);
        Assert.Contains("warn  env-secrets: still in .env: HCP_TOKEN", lines);
        Assert.Contains("ok    github: the App ai-factory-sim on test (all repositories), signed in as test", lines);

        all.Remove("workflows");
        File.WriteAllText(Path.Combine(gh.GitBase, "installations.json"), new JsonArray(new JsonObject { ["id"] = 9, ["account"] = "test", ["selection"] = "all", ["permissions"] = all.DeepClone() }).ToJsonString());
        var (code, again) = Doctor(env);

        Assert.Equal(1, code);
        Assert.Contains("fail  github: the installation on test lacks workflows: write", again.Split('\n'));
    }

    [Fact]
    public async Task The_Foundry_probe_sends_one_token_with_the_az_token_and_reads_a_refusal()
    {
        var port = StubHost.FreePort();
        var seen = new List<JsonObject>();
        await using var stub = StubHost.Build(port, async ctx =>
        {
            var body = JsonNode.Parse(await StubHost.BodyText(ctx))!.AsObject();
            seen.Add(new JsonObject { ["path"] = ctx.Request.Path.Value, ["auth"] = ctx.Request.Headers.Authorization.ToString(), ["version"] = ctx.Request.Headers["anthropic-version"].ToString(), ["body"] = body });
            return (string?)body["model"] == "missing"
                ? StubReply.Json(404, new JsonObject { ["error"] = new JsonObject { ["message"] = "DeploymentNotFound" } })
                : StubReply.Json(200, new JsonObject { ["type"] = "message" });
        });
        await stub.StartAsync(TestContext.Current.CancellationToken);
        var az = new FakeAzRunner("entra-token");
        var envFile = _dir["foundry.env"];
        File.WriteAllText(envFile, $"FACTORY_FOUNDRY_URL=http://localhost:{port}");
        var probe = new FoundryProbe(new HttpClient(), az, new DotEnvFile(envFile));

        Assert.Null(probe.Ping("fdy", "claude-opus-5-5"));
        Assert.Equal("404 DeploymentNotFound", probe.Ping("fdy", "missing"));

        Assert.Equal(("/anthropic/v1/messages", "Bearer entra-token", "2026-10-21", 1), ((string)seen[0]["path"]!, (string)seen[0]["auth"]!, (string)seen[0]["version"]!, (int)seen[0]["body"]!["max_tokens"]!));
        Assert.Contains("https://ai.azure.com", Assert.Single(az.Calls));
    }
}
