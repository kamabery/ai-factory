using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

/// <summary>doctor's github check, on the App and on the PAT fallback.</summary>
public class GitHubCheckTests
{
    private static readonly string Pem = MakePem();
    private readonly ISecrets _secrets = Substitute.For<ISecrets>();
    private readonly IGitHubApp _app = Substitute.For<IGitHubApp>();
    private readonly IGitHubCredential _cred = Substitute.For<IGitHubCredential>();
    private readonly IGitHub _github = Substitute.For<IGitHub>();
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();
    private readonly MemoryFiles _files = new();
    private readonly IFactoryPaths _paths = Substitute.For<IFactoryPaths>();

    private static string MakePem() { using var rsa = RSA.Create(2048); return rsa.ExportRSAPrivateKeyPem(); }

    public GitHubCheckTests()
    {
        _env.Get(Arg.Any<string>()).Returns((string?)null);
        _env.Get("GITHUB_OWNER").Returns("kamabery");
        _secrets.Get(Arg.Any<string>()).Returns((string?)null);
        _secrets.Get("GITHUB_APP_ID").Returns("7");
        _secrets.Get("GITHUB_APP_PRIVATE_KEY").Returns(Pem);
        _cred.IsApp.Returns(true);
        _app.App(Arg.Any<string>()).Returns(new JsonObject { ["slug"] = "ai-factory-kamabery" });
        _github.UserLogin().Returns("kamabery");
        var scripts = Path.Combine(Path.GetTempPath(), "repo", "factory", "scripts");
        _paths.ScriptsDir.Returns(scripts);
        _files.Put(Path.Combine(Path.GetTempPath(), "repo", "factory", "github-app.json"), """{"default_permissions":{"contents":"write","workflows":"write","metadata":"read"}}""");
    }

    private void Installed(string account, string selection, params (string, string)[] permissions) =>
        _app.Installations(Arg.Any<string>()).Returns([new AppInstallation(42, account, selection, permissions.ToDictionary(p => p.Item1, p => p.Item2))]);

    private DoctorResult Check() =>
        new GitHubCheck(_secrets, _app, _cred, _github, _env, new AppManifest(_paths, _files), new FakeTimeProvider(DateTimeOffset.UtcNow)).Check();

    [Fact]
    public void Passes_when_the_App_is_installed_on_all_repositories_with_every_permission()
    {
        Installed("kamabery", "all", ("contents", "write"), ("workflows", "write"), ("metadata", "read"));

        Assert.Equal(DoctorResult.Ok("the App ai-factory-kamabery on kamabery (all repositories), signed in as kamabery"), Check());
        _cred.Received(1).Token("kamabery");
    }

    [Fact]
    public void Not_installed_on_the_owner_says_where_to_install_it()
    {
        Installed("someone-else", "all");

        Assert.Equal(DoctorResult.Fail("the App ai-factory-kamabery is not installed on kamabery",
            "install it on All repositories: https://github.com/apps/ai-factory-kamabery/installations/new"), Check());
    }

    [Fact]
    public void An_installation_on_selected_repositories_fails()
    {
        Installed("kamabery", "selected", ("contents", "write"), ("workflows", "write"), ("metadata", "read"));

        var r = Check();

        Assert.Equal(DoctorStatus.Fail, r.Status);
        Assert.Equal("set Repository access to All repositories: https://github.com/settings/installations/42", r.Fix);
    }

    [Fact]
    public void A_permission_missing_or_only_read_is_named()
    {
        Installed("kamabery", "all", ("contents", "read"), ("metadata", "read"));

        Assert.Equal("the installation on kamabery lacks contents: write, workflows: write", Check().Detail);
    }

    [Fact]
    public void A_GitHub_refusal_fails_with_init_as_the_fix()
    {
        _app.App(Arg.Any<string>()).Throws(new FactoryException("GitHub 401 (GET /app): Bad credentials"));

        Assert.Equal(DoctorResult.Fail("GitHub 401 (GET /app): Bad credentials", "factory init"), Check());
    }

    [Fact]
    public void The_PAT_fallback_is_a_warn_when_it_works_and_a_fail_when_it_does_not()
    {
        _cred.IsApp.Returns(false);
        Assert.Equal(DoctorResult.Warn("using the GitHub PAT fallback, signed in as kamabery", "factory init sets up the GitHub App"), Check());

        _github.UserLogin().Throws(new FactoryException("GitHub 401: Bad credentials"));
        Assert.Equal(DoctorResult.Fail("no GitHub App, and the GitHub PAT does not work: GitHub 401: Bad credentials", "factory init"), Check());
    }
}
