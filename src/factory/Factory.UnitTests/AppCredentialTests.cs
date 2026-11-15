using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using System.Security.Cryptography;

namespace Factory.UnitTests;

public class AppCredentialTests
{
    private static readonly string Local = Path.Combine(Path.GetTempPath(), "app-local");
    private static readonly string Pem = MakePem();

    private readonly MemoryFiles _files = new();
    private readonly IGitHubApp _app = Substitute.For<IGitHubApp>();
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();
    private readonly Dictionary<string, string> _secrets = new() { ["GITHUB_APP_ID"] = "7", ["GITHUB_APP_PRIVATE_KEY"] = Pem, ["GITHUB_APP_CLIENT_ID"] = "cid", ["GITHUB_APP_CLIENT_SECRET"] = "csec" };
    private readonly ISecretStore _store = Substitute.For<ISecretStore>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

    public AppCredentialTests()
    {
        _env.Get(Arg.Any<string>()).Returns((string?)null);
        _env.Get("GITHUB_OWNER").Returns("kamabery");
        _app.Installations(Arg.Any<string>()).Returns([
            new AppInstallation(11, "kamabery", "all", new Dictionary<string, string>()),
            new AppInstallation(22, "Contoso", "all", new Dictionary<string, string>())]);
        _app.InstallationToken(Arg.Any<string>(), Arg.Any<long>()).Returns(c => new AppToken($"ghs-{c.ArgAt<long>(1)}-{_clock.GetUtcNow():HHmm}", _clock.GetUtcNow().AddHours(1)));
    }

    private static string MakePem() { using var rsa = RSA.Create(2048); return rsa.ExportRSAPrivateKeyPem(); }

    private AppCredential Sut()
    {
        var secrets = Substitute.For<ISecrets>();
        secrets.Get(Arg.Any<string>()).Returns(c => _secrets.GetValueOrDefault(c.Arg<string>()));
        _store.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>())).Do(c => _secrets[c.ArgAt<string>(0)] = c.ArgAt<string>(1));
        return new AppCredential(secrets, _store, _env, _app, _files, Options.Create(new Factory.Store.StateOptions { LocalDir = Local }), _clock);
    }

    [Fact]
    public void Mints_the_owners_installation_token_matching_the_account_without_case()
    {
        Assert.Equal("ghs-22-1200", Sut().Token("contoso"));
        Assert.Equal("ghs-11-1200", Sut().Token());
    }

    [Fact]
    public void Caches_the_token_for_later_processes_until_five_minutes_before_it_expires()
    {
        Sut().Token();
        _clock.Advance(TimeSpan.FromMinutes(54));

        Assert.Equal("ghs-11-1200", Sut().Token());
        Assert.NotNull(_files.Text(Path.Combine(Local, "github-token-kamabery.json")));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("ghs-11-1256", Sut().Token());
        _app.Received(2).InstallationToken(Arg.Any<string>(), 11);
    }

    [Fact]
    public void An_owner_with_no_installation_is_refused_and_says_how_to_fix_it()
    {
        Assert.Equal("the GitHub App is not installed on acme; install it (factory init), or check GITHUB_OWNER",
            Assert.Throws<FactoryException>(() => Sut().Token("acme")).Message);
    }

    [Fact]
    public void The_user_token_is_returned_until_five_minutes_before_it_expires_then_refreshed_and_kept()
    {
        _secrets["GITHUB_USER_TOKEN"] = "ghu-1";
        _secrets["GITHUB_USER_REFRESH_TOKEN"] = "ghr-1";
        _secrets["GITHUB_USER_TOKEN_EXPIRES"] = _clock.GetUtcNow().AddMinutes(6).ToUnixTimeSeconds().ToString();
        _app.Refresh("cid", "csec", "ghr-1").Returns(new UserTokens("ghu-2", "ghr-2", _clock.GetUtcNow().AddHours(8)));

        Assert.Equal("ghu-1", Sut().UserToken());
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("ghu-2", Sut().UserToken());

        Assert.Equal(("ghu-2", "ghr-2"), (_secrets["GITHUB_USER_TOKEN"], _secrets["GITHUB_USER_REFRESH_TOKEN"]));
        Assert.Equal("ghu-2", Sut().UserToken());
        _app.Received(1).Refresh("cid", "csec", "ghr-1");
    }

    [Fact]
    public void An_expired_user_token_with_no_refresh_token_says_to_run_init()
    {
        _secrets["GITHUB_USER_TOKEN"] = "ghu-1";
        _secrets["GITHUB_USER_TOKEN_EXPIRES"] = _clock.GetUtcNow().ToUnixTimeSeconds().ToString();

        Assert.Equal("the GitHub App's user token expired and there is no refresh token; run factory init",
            Assert.Throws<FactoryException>(() => Sut().UserToken()).Message);
    }

    [Fact]
    public void The_credential_is_the_App_once_GITHUB_APP_ID_is_set_else_the_PAT()
    {
        var secrets = Substitute.For<ISecrets>();
        secrets.Get("GITHUB_TOKEN").Returns("pat");
        var both = new GitHubCredential(secrets, new PatCredential(secrets), Sut());

        Assert.Equal((false, "pat", "pat"), (both.IsApp, both.Token("x"), both.UserToken()));
        secrets.Get("GITHUB_APP_ID").Returns("7");
        Assert.True(both.IsApp);
    }
}
