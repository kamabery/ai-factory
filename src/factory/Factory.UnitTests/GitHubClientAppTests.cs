using System.Net;
using System.Text;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

/// <summary>GitHubClient on the GitHub App's credential (contract, "Targets" and "Secrets, init and doctor").</summary>
public class GitHubClientAppTests
{
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();
    private readonly IGitHubCredential _cred = Substitute.For<IGitHubCredential>();
    private readonly StubHttpHandler _http = new();
    private readonly List<(string Url, string? Auth)> _calls = [];

    public GitHubClientAppTests()
    {
        _env.Get(Arg.Any<string>()).Returns((string?)null);
        _env.Get("GITHUB_API_URL").Returns("http://gh");
        _cred.Token(Arg.Any<string?>()).Returns(c => $"inst-{c.Arg<string?>() ?? "default"}");
        _cred.UserToken().Returns("user-tok");
    }

    private GitHubClient Sut => new(new HttpClient(_http), _env, _cred);

    private void Reply(Func<string, (int, string)> route) => _http.Reply = r =>
    {
        _calls.Add((r.RequestUri!.PathAndQuery, r.Headers.Authorization?.Parameter));
        var (status, body) = route(r.RequestUri.PathAndQuery);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    };

    [Fact]
    public void Each_call_uses_the_installation_token_of_the_owner_its_path_names()
    {
        Reply(_ => (200, "{}"));

        Sut.Send(HttpMethod.Get, "/repos/acme/books/branches/main");
        Sut.Send(HttpMethod.Post, "/orgs/Contoso/repos");
        Sut.Send(HttpMethod.Get, "/installation/repositories");

        Assert.Equal(["inst-acme", "inst-Contoso", "inst-default"], _calls.Select(c => c.Auth));
    }

    [Fact]
    public void A_credential_that_refuses_is_status_0_with_its_reason_and_no_call()
    {
        _cred.Token("acme").Throws(new FactoryException("the GitHub App is not installed on acme; install it (factory init), or check GITHUB_OWNER"));

        var r = Sut.Send(HttpMethod.Get, "/repos/acme/books");

        Assert.Equal("GitHub 0: the GitHub App is not installed on acme; install it (factory init), or check GITHUB_OWNER", GitHubErrors.Text(r));
        Assert.Empty(_calls);
    }

    [Fact]
    public void The_apps_repos_are_its_installations_newest_push_first()
    {
        Reply(_ => (200, """{"repositories":[{"full_name":"o/old","pushed_at":"2026-01-01T00:00:00Z"},{"full_name":"o/new","pushed_at":"2026-11-01T00:00:00Z"}]}"""));

        Assert.Equal(["o/new", "o/old"], Sut.OwnedRepos());
        Assert.Equal("/installation/repositories?per_page=100", _calls.Single().Url);
    }

    [Fact]
    public void A_repo_is_created_as_the_human_with_the_user_token()
    {
        Reply(p => p == "/user" ? (200, """{"login":"kamabery"}""") : (201, "{}"));

        Assert.Null(Sut.CreateRepo("kamabery/books"));

        Assert.Equal([("/user", "user-tok"), ("/user/repos", "user-tok")], _calls);
    }

    [Fact]
    public void A_refused_creation_says_to_create_the_repo_by_hand()
    {
        Reply(p => p == "/user" ? (200, """{"login":"kamabery"}""") : (403, """{"message":"Resource not accessible by integration"}"""));

        Assert.Equal("GitHub refused to create kamabery/books (GitHub 403: Resource not accessible by integration); create it, empty, then approve the job again",
            Sut.CreateRepo("kamabery/books"));
    }

    [Fact]
    public void No_user_token_says_to_run_init()
    {
        _cred.UserToken().Returns((string?)null);

        Assert.Equal("GitHub refused to create o/n (the GitHub App has no user token; run factory init); create it, empty, then approve the job again",
            Sut.CreateRepo("o/n"));
        Assert.Empty(_calls);
    }
}
