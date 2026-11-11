using System.Net;
using System.Text;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class GitHubClientTests
{
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();
    private readonly IGitHubCredential _cred = Substitute.For<IGitHubCredential>();
    private readonly StubHttpHandler _http = new();

    public GitHubClientTests()
    {
        _env.Get(Arg.Any<string>()).Returns((string?)null);
        _cred.Token().Returns("tok");
        _cred.Missing.Returns("GITHUB_TOKEN is not set in .env");
    }

    private GitHubClient Sut => new(new HttpClient(_http), _env, _cred);

    private static HttpResponseMessage Res(int status, string body = "", (string, string)? header = null)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (header is { } h) r.Headers.TryAddWithoutValidation(h.Item1, h.Item2);
        return r;
    }

    [Fact]
    public void Sends_the_token_as_a_bearer_header_with_GitHubs_media_type_version_and_a_user_agent()
    {
        _env.Get("GITHUB_API_URL").Returns("http://gh/");
        string? sent = null;
        _http.Reply = r =>
        {
            Assert.Equal("http://gh/user/repos", r.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString());
            Assert.Equal("application/vnd.github+json", r.Headers.Accept.Single().ToString());
            Assert.Equal("2022-11-28", r.Headers.GetValues("X-GitHub-Api-Version").Single());
            Assert.NotEmpty(r.Headers.UserAgent);
            sent = r.Content!.ReadAsStringAsync().Result;
            return Res(201, """{"full_name":"o/n"}""");
        };

        var res = Sut.Send(HttpMethod.Post, "/user/repos", new System.Text.Json.Nodes.JsonObject { ["name"] = "n", ["private"] = true });

        Assert.Equal("""{"name":"n","private":true}""", sent);
        Assert.Equal((201, "o/n", null), (res.Status, (string?)res.Body!["full_name"], res.Permission));
    }

    [Fact]
    public void Without_a_credential_there_is_no_answer_and_no_call()
    {
        _cred.Token().Returns((string?)null);

        var res = Sut.Send(HttpMethod.Get, "/user");

        Assert.Equal("GitHub 0: GITHUB_TOKEN is not set in .env", GitHubErrors.Text(res));
        Assert.Equal(0, _http.Calls);
    }

    [Fact]
    public void Defaults_to_api_github_com()
    {
        _http.Reply = r => { Assert.Equal("https://api.github.com/user", r.RequestUri!.ToString()); return Res(200, "{}"); };

        Assert.Equal(200, Sut.Send(HttpMethod.Get, "/user").Status);
    }

    [Fact]
    public void A_403_carries_the_permission_GitHub_says_the_credential_lacks()
    {
        _http.Reply = _ => Res(403, """{"message":"denied"}""", ("X-Accepted-GitHub-Permissions", "environments=write"));

        var res = Sut.Send(HttpMethod.Put, "/repos/o/n/environments/j001");

        Assert.Equal("GitHub 403: denied; " + GitHubErrors.PatNeeds("environments=write"), GitHubErrors.Text(res));
    }

    [Fact]
    public void Only_a_403_reads_the_permission_header_and_a_text_body_is_the_message()
    {
        _http.Reply = _ => Res(500, "oops", ("X-Accepted-GitHub-Permissions", "x=write"));

        Assert.Equal("GitHub 500: oops", GitHubErrors.Text(Sut.Send(HttpMethod.Get, "/x")));
    }

    [Fact]
    public void A_failed_request_is_no_answer_with_its_reason()
    {
        _http.Reply = _ => throw new HttpRequestException("connection refused");

        Assert.Equal("GitHub 0: connection refused", GitHubErrors.Text(Sut.Send(HttpMethod.Get, "/user")));
    }

    [Theory]
    [InlineData(200, 200, RepoState.Existing)]
    [InlineData(404, 0, RepoState.Missing)]
    [InlineData(200, 404, RepoState.Empty)]
    public void Lookup_is_existing_with_main_missing_or_empty_without(int repo, int main, RepoState state)
    {
        _http.Reply = r => r.RequestUri!.AbsolutePath switch
        {
            "/repos/o/n" => Res(repo, "{}"),
            "/repos/o/n/branches/main" => Res(main, "{}"),
            var p => throw new InvalidOperationException(p),
        };

        Assert.Equal(state, Sut.Lookup("o/n"));
    }

    [Theory]
    [InlineData(500, 200)]
    [InlineData(200, 500)]
    public void Lookup_refuses_with_GitHubs_error(int repo, int main)
    {
        _http.Reply = r => Res(r.RequestUri!.AbsolutePath.EndsWith("/main", StringComparison.Ordinal) ? main : repo, """{"message":"boom"}""");

        Assert.Equal("GitHub 500: boom", Assert.Throws<FactoryException>(() => Sut.Lookup("o/n")).Message);
    }

    [Fact]
    public void OwnedRepos_are_one_page_of_the_users_own_newest_push_first()
    {
        _http.Reply = r =>
        {
            Assert.Equal("/user/repos?affiliation=owner&sort=pushed&direction=desc&per_page=100", r.RequestUri!.PathAndQuery);
            return Res(200, """[{"full_name":"o/new"},{"full_name":"o/old"}]""");
        };

        Assert.Equal(["o/new", "o/old"], Sut.OwnedRepos());
    }

    [Fact]
    public void OwnedRepos_refuses_with_GitHubs_error()
    {
        _http.Reply = _ => Res(401, """{"message":"Bad credentials"}""");

        Assert.Equal("GitHub 401: Bad credentials", Assert.Throws<FactoryException>(Sut.OwnedRepos).Message);
    }

    [Fact]
    public void Clones_from_github_com_or_FACTORY_GIT_BASE()
    {
        Assert.Equal("https://github.com/o/n.git", Sut.CloneUrl("o/n"));
        _env.Get("FACTORY_GIT_BASE").Returns(@"file:///D:/gh\");
        Assert.Equal("file:///D:/gh/o/n.git", Sut.CloneUrl("o/n"));
    }

    [Fact]
    public void Git_gets_the_token_as_a_one_command_basic_header_and_no_credential_helper()
    {
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes("x-access-token:tok"));

        Assert.Equal(["-c", "credential.helper=", "-c", $"http.extraHeader=Authorization: Basic {basic}"], Sut.GitAuth());
    }

    [Fact]
    public void CreateRepo_creates_under_the_user_when_it_is_the_owner_else_in_the_org()
    {
        var posts = new List<string>();
        _http.Reply = r =>
        {
            if (r.Method == HttpMethod.Get) return Res(200, """{"login":"Me"}""");
            posts.Add(r.RequestUri!.AbsolutePath + " " + r.Content!.ReadAsStringAsync().Result);
            return Res(201, "{}");
        };

        Assert.Null(Sut.CreateRepo("me/app"));
        Assert.Null(Sut.CreateRepo("org/app"));

        Assert.Equal(["""/user/repos {"name":"app","private":true,"auto_init":false}""", """/orgs/org/repos {"name":"app","private":true,"auto_init":false}"""], posts);
    }

    [Fact]
    public void CreateRepo_says_why_not()
    {
        _http.Reply = r => r.Method == HttpMethod.Get ? Res(401, """{"message":"Bad credentials"}""") : Res(201);
        Assert.Equal("cannot create o/app: GitHub 401: Bad credentials", Sut.CreateRepo("o/app"));

        _http.Reply = r => r.Method == HttpMethod.Get ? Res(200, """{"login":"o"}""") : Res(422, """{"message":"name already exists"}""");
        Assert.Equal("cannot create o/app: GitHub 422: name already exists", Sut.CreateRepo("o/app"));
    }

    [Fact]
    public void OpenPr_asks_for_the_open_PR_from_the_owners_branch()
    {
        string? asked = null;
        _http.Reply = r => { asked = r.RequestUri!.PathAndQuery; return Res(200, """[{"number":7,"html_url":"https://gh/o/r/pull/7"}]"""); };

        Assert.Equal(new PullRequest(7, "https://gh/o/r/pull/7"), Sut.OpenPr("o/r", "factory/J-001"));
        Assert.Equal("/repos/o/r/pulls?state=open&head=o:factory/J-001", asked);

        _http.Reply = _ => Res(200, "[]");
        Assert.Null(Sut.OpenPr("o/r", "factory/J-001"));
        _http.Reply = _ => Res(500, "{}");
        Assert.Null(Sut.OpenPr("o/r", "factory/J-001"));
    }

    [Fact]
    public void Pr_is_GitHubs_PR_or_null()
    {
        _http.Reply = _ => Res(200, """{"merged":true,"merge_commit_sha":"abc"}""");
        Assert.Equal("abc", (string?)Sut.Pr("o/r", 3)!["merge_commit_sha"]);

        _http.Reply = _ => Res(404, """{"message":"Not Found"}""");
        Assert.Null(Sut.Pr("o/r", 3));
    }
}
