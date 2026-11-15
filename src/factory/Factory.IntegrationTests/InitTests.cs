using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.IntegrationTests;

/// <summary>
/// factory init on a machine with nothing set up (contract, "Secrets, init and doctor"): the manifest exchange, the install
/// and the sign-in, against the GitHub stub, with a test browser in the human's place and no .env edit by hand. The run is
/// in cloud mode with no resource, so it does not depend on this machine's claude login: init leaves that to doctor, which
/// fails on it.
/// </summary>
[Collection(GitHubStubCollection.Name)]
public sealed class InitTests : IDisposable
{
    private readonly GitHubStub _gh;
    private readonly TempDir _dir = new();
    private readonly TestBrowser _browser = new();
    private readonly RecordingLauncher _launcher = new();

    public InitTests(GitHubStub gh)
    {
        _gh = gh;
        foreach (var f in new[] { "app.json", "installations.json", "pending-manifest.json", "install-selected" })
            if (Directory.Exists(gh.GitBase)) File.Delete(Path.Combine(gh.GitBase, f));
        Directory.CreateDirectory(_dir["repo"]);
        File.WriteAllLines(Path.Combine(_dir["repo"], ".env.example"),
            ["GITHUB_OWNER=", $"GITHUB_API_URL=http://localhost:{gh.Port}", $"GITHUB_WEB_URL=http://localhost:{gh.Port}", "FACTORY_MODE=cloud"]);
    }

    public void Dispose()
    {
        foreach (var f in new[] { "app.json", "installations.json", "pending-manifest.json", "install-selected" })
            if (Directory.Exists(_gh.GitBase)) File.Delete(Path.Combine(_gh.GitBase, f));
        _dir.Dispose();
    }

    private string EnvFile => Path.Combine(_dir["repo"], ".env");

    private (int Code, string Out) Init(params string[] args)
    {
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = _dir["state"], ["FACTORY_ENV_FILE"] = EnvFile, ["FACTORY_SECRET_DIR"] = _dir["secrets"], ["FACTORY_LOCAL_DIR"] = _dir["local"],
            ["FACTORY_SCRIPTS"] = GitHubStub.RepoFile("factory", "scripts"), ["FACTORY_INIT_WAIT_SECONDS"] = "3", ["FACTORY_CLI"] = GitHubStub.RepoFile("artifacts", "bin", "Factory.Cli", "debug", "factory.dll"),
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"), ["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            ["USERPROFILE"] = Environment.GetEnvironmentVariable("USERPROFILE"), ["HOME"] = Environment.GetEnvironmentVariable("HOME"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"), ["XDG_RUNTIME_DIR"] = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
        };
        var services = new ServiceCollection().AddFactoryCli(new CliEnvironment(env, _dir.Path, "\n", _dir["appdata"], Path.GetTempPath(), _dir.Path));
        services.AddSingleton<IBrowser>(_browser);
        services.AddSingleton<IFactoryLauncher>(_launcher);
        using var sp = Composition.Build(services);
        var output = new StringWriter { NewLine = "\n" };
        var code = sp.GetRequiredService<IEnumerable<IStandaloneCommand>>().Single(c => c.Name == "init").Run(args, TextReader.Null, output, output);
        return (code, output.ToString());
    }

    [Fact]
    public void From_nothing_creates_installs_and_signs_in_to_the_App_and_writes_only_non_secrets_to_env()
    {
        var (code, output) = Init("--no-open");

        var secrets = new FileSecretStore(_dir["secrets"]);
        Assert.Equal(("1001", "ai-factory-sim", "Iv1.sim"), (secrets.Get(SecretNames.AppId), secrets.Get(SecretNames.AppSlug), secrets.Get(SecretNames.AppClientId)));
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", secrets.Get(SecretNames.AppPrivateKey), StringComparison.Ordinal);
        Assert.StartsWith("ghu_", secrets.Get(SecretNames.UserToken), StringComparison.Ordinal);
        Assert.Contains("GITHUB_OWNER=test", File.ReadAllLines(EnvFile));
        Assert.DoesNotContain(File.ReadAllLines(EnvFile), l => l.StartsWith("GITHUB_APP", StringComparison.Ordinal) || l.Contains("ghu_", StringComparison.Ordinal));
        Assert.Equal(3, _browser.Opened.Count);
        Assert.EndsWith("/start", _browser.Opened[0], StringComparison.Ordinal);
        Assert.EndsWith("/apps/ai-factory-sim/installations/new", _browser.Opened[1], StringComparison.Ordinal);
        Assert.Contains("/login/oauth/authorize?client_id=Iv1.sim", _browser.Opened[2], StringComparison.Ordinal);
        Assert.Contains("ok    github: the App ai-factory-sim on test (all repositories), signed in as test", output.ReplaceLineEndings("\n").Split('\n'));
        Assert.Contains("fail  llm: cloud mode: no FACTORY_FOUNDRY_RESOURCE", output.ReplaceLineEndings("\n").Split('\n'));
        Assert.Equal(1, code);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public void Run_again_it_skips_what_is_done_and_opens_nothing()
    {
        Init("--no-open");
        _browser.Opened.Clear();

        var (_, output) = Init("--no-open");

        Assert.Empty(_browser.Opened);
        Assert.Contains("ok    github: the App ai-factory-sim on test", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_install_on_selected_repositories_only_is_refused()
    {
        Directory.CreateDirectory(_gh.GitBase);
        File.WriteAllText(Path.Combine(_gh.GitBase, "install-selected"), "");

        var (code, output) = Init("--no-open");

        Assert.Equal(1, code);
        Assert.Contains("init refused: the App is installed on selected repositories of test; set Repository access to All repositories, then run factory init again", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_callback_with_another_state_is_refused_and_no_App_is_kept()
    {
        _browser.ForgeState = true;

        var (code, output) = Init("--no-open");

        Assert.Equal(1, code);
        Assert.Contains("init refused: the GitHub callback's state does not match this run", output, StringComparison.Ordinal);
        Assert.Null(new FileSecretStore(_dir["secrets"]).Get(SecretNames.AppId));
    }

    [Fact]
    public void Unknown_flags_are_refused_with_the_usage()
    {
        Assert.Equal((1, "factory: usage: factory init [--org <org>] [--foundry <resource>] [--move-env-secrets] [--no-open] (not --bogus)\n"), Init("--bogus"));
    }
}
