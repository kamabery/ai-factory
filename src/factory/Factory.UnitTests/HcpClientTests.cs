using System.Net;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class HcpClientTests
{
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();
    private readonly StubHttpHandler _http = new();
    private readonly List<(string Method, string Url, string? Body)> _sent = [];

    public HcpClientTests() => _env.Get(Arg.Any<string>()).Returns((string?)null);

    private HcpClient Sut => new(new HttpClient(_http), _env, EnvSecrets.Over(_env));

    /// <summary>HCP at http://hcp/ with a token and an org; <paramref name="route"/> answers each request (status, body).</summary>
    private void Hcp(Func<string, string, (int Status, string Body)> route)
    {
        _env.Get("HCP_TOKEN").Returns("tok");
        _env.Get("HCP_ORG").Returns("org");
        _env.Get("HCP_API_URL").Returns("http://hcp/");
        _http.Reply = r =>
        {
            var body = r.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            _sent.Add((r.Method.Method, r.RequestUri!.ToString(), body));
            var (status, text) = route(r.Method.Method, r.RequestUri!.PathAndQuery);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text) };
        };
    }

    [Fact]
    public void Reads_the_varsets_variables_with_the_team_token()
    {
        _env.Get("HCP_AZURE_VARSET").Returns("vs-1");
        _env.Get("HCP_TOKEN").Returns("tok");
        _env.Get("HCP_API_URL").Returns("http://hcp/");
        _http.Respond = r =>
        {
            Assert.Equal("http://hcp/api/v2/varsets/vs-1/relationships/vars", r.RequestUri!.ToString());
            Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString());
            return """{"data":[{"attributes":{"category":"env","key":"K","value":"v","sensitive":false}},{"attributes":{"category":"terraform","key":"S","value":null,"sensitive":true}}]}""";
        };

        Assert.Equal([new HcpVar("env", "K", "v", false), new HcpVar("terraform", "S", null, true)], Sut.VarsetVars());
    }

    [Fact]
    public void No_varset_is_no_variables_and_no_call()
    {
        Assert.Empty(Sut.VarsetVars());
        Assert.Equal(0, _http.Calls);
    }

    [Fact]
    public void Needs_a_token()
    {
        _env.Get("HCP_AZURE_VARSET").Returns("vs-1");

        Assert.Equal("HCP_TOKEN is not set in .env", Assert.Throws<FactoryException>(Sut.VarsetVars).Message);
    }

    [Fact]
    public void EnsureProject_finds_the_project_by_name_and_attaches_the_varset()
    {
        _env.Get("HCP_AZURE_VARSET").Returns("vs-1");
        Hcp((m, p) => p.StartsWith("/api/v2/organizations/org/projects", StringComparison.Ordinal)
            ? (200, """{"data":[{"id":"prj-0","attributes":{"name":"todo-other"}},{"id":"prj-1","attributes":{"name":"TODO"}}]}""")
            : (204, ""));

        Assert.Equal("prj-1", Sut.EnsureProject("todo"));

        Assert.Equal("http://hcp/api/v2/organizations/org/projects?filter%5Bnames%5D=todo", _sent[0].Url);
        Assert.Equal(("POST", "http://hcp/api/v2/varsets/vs-1/relationships/projects"), (_sent[1].Method, _sent[1].Url));
        Assert.Equal("""{"data":[{"type":"projects","id":"prj-1"}]}""", _sent[1].Body);
    }

    [Fact]
    public void EnsureProject_creates_a_missing_project_and_needs_no_varset()
    {
        Hcp((m, p) => m == "GET" ? (200, """{"data":[]}""") : (201, """{"data":{"id":"prj-9"}}"""));

        Assert.Equal("prj-9", Sut.EnsureProject("todo"));

        Assert.Equal(2, _sent.Count);
        Assert.Equal("""{"data":{"type":"projects","attributes":{"name":"todo"}}}""", _sent[1].Body);
    }

    [Fact]
    public void EnsureWorkspace_creates_it_in_the_project_with_its_timer()
    {
        Hcp((m, p) => m == "GET" ? (404, """{"errors":[{"status":"404"}]}""") : (201, """{"data":{"id":"ws-1"}}"""));

        Assert.Equal("ws-1", Sut.EnsureWorkspace("todo-j001", "prj-1", "2026-11-11T12:00:00Z"));

        Assert.Equal(("POST", "http://hcp/api/v2/organizations/org/workspaces"), (_sent[1].Method, _sent[1].Url));
        Assert.Equal("""{"data":{"type":"workspaces","attributes":{"name":"todo-j001","execution-mode":"remote","auto-destroy-at":"2026-11-11T12:00:00Z"},"relationships":{"project":{"data":{"type":"projects","id":"prj-1"}}}}}""",
            _sent[1].Body);
    }

    [Fact]
    public void EnsureWorkspace_pushes_an_existing_workspaces_timer_out_and_leaves_an_idle_one_alone()
    {
        Hcp((m, p) => m == "GET" ? (200, """{"data":{"id":"ws-2"}}""") : (200, "{}"));

        Assert.Equal("ws-2", Sut.EnsureWorkspace("todo-j001", "prj-1", "2026-11-11T12:00:00Z"));
        Assert.Equal(("PATCH", "http://hcp/api/v2/workspaces/ws-2"), (_sent[1].Method, _sent[1].Url));
        Assert.Equal("""{"data":{"type":"workspaces","attributes":{"auto-destroy-at":"2026-11-11T12:00:00Z"}}}""", _sent[1].Body);

        _sent.Clear();
        Assert.Equal("ws-2", Sut.EnsureWorkspace("todo-dev", "prj-1"));
        Assert.Single(_sent);
    }

    [Fact]
    public void WorkspaceId_is_null_for_a_missing_workspace()
    {
        Hcp((m, p) => p.EndsWith("/gone", StringComparison.Ordinal) ? (404, "") : (200, """{"data":{"id":"ws-3"}}"""));

        Assert.Equal("ws-3", Sut.WorkspaceId("here"));
        Assert.Null(Sut.WorkspaceId("gone"));
    }

    [Fact]
    public void Outputs_fetch_a_sensitive_value_only_when_it_is_asked_for()
    {
        Hcp((m, p) => p switch
        {
            "/api/v2/workspaces/ws-1/current-state-version-outputs" => (200, """
                {"data":[{"id":"o-1","attributes":{"name":"api_url","sensitive":false,"value":"https://api"}},
                         {"id":"o-2","attributes":{"name":"qa_password","sensitive":true,"value":null}},
                         {"id":"o-3","attributes":{"name":"db_password","sensitive":true,"value":null}}]}
                """),
            "/api/v2/state-version-outputs/o-2" => (200, """{"data":{"attributes":{"value":"pw"}}}"""),
            _ => (500, "{}"),
        });

        var outs = Sut.Outputs("ws-1", ["QA_PASSWORD"]);

        Assert.Equal(("https://api", "pw", null), (outs["api_url"], outs["qa_password"], outs["db_password"]));
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void RunLog_reads_the_latest_runs_plan_and_apply_logs_from_their_urls()
    {
        Hcp((m, p) => p switch
        {
            "/api/v2/workspaces/ws-1/runs?page%5Bsize%5D=1" => (200, """
                {"data":[{"id":"run-1","attributes":{"status":"errored"},"relationships":{"plan":{"data":{"id":"plan-1"}},"apply":{"data":{"id":null}}}}]}
                """),
            "/api/v2/plans/plan-1" => (200, """{"data":{"attributes":{"log-read-url":"http://logs/plan-1"}}}"""),
            "/plan-1" => (200, "Error: boom"),
            _ => (500, "{}"),
        });

        Assert.Equal("run run-1: errored\n--- plans\nError: boom", Sut.RunLog("ws-1"));
        Assert.Null(_sent.Single(s => s.Url == "http://logs/plan-1").Body);
    }

    [Fact]
    public void RunLog_says_so_when_the_workspace_has_no_run()
    {
        Hcp((m, p) => (200, """{"data":[]}"""));
        Assert.Equal("(no HCP run)", Sut.RunLog("ws-1"));
    }

    [Fact]
    public void QueueDestroy_queues_an_auto_applied_destroy_and_Run_reads_it_back()
    {
        Hcp((m, p) => (m, p) switch
        {
            ("POST", "/api/v2/runs") => (201, """{"data":{"id":"run-5","attributes":{"status":"pending"}}}"""),
            _ => (200, """{"data":{"id":"run-5","attributes":{"status":"applied"}}}"""),
        });

        Assert.Equal(new HcpRun("run-5", "pending"), Sut.QueueDestroy("ws-1"));
        Assert.Equal("""{"data":{"type":"runs","attributes":{"is-destroy":true,"auto-apply":true,"message":"factory: tear down the preview"},"relationships":{"workspace":{"data":{"type":"workspaces","id":"ws-1"}}}}}""",
            _sent[0].Body);
        Assert.True(Sut.Run("run-5").IsFinal);
        Assert.False(new HcpRun("r", "applying").IsFinal);
    }

    [Fact]
    public void A_refusal_is_one_line_with_HCPs_own_errors()
    {
        Hcp((m, p) => (409, """{"errors":[{"status":"409","title":"conflict","detail":"Latest workspace state is being processed"},{"title":"second"}]}"""));

        Assert.Equal("HCP 409 on POST /workspaces/ws-1/actions/safe-delete: Latest workspace state is being processed; second",
            Assert.Throws<FactoryException>(() => Sut.SafeDelete("ws-1")).Message);
        Assert.Null(_sent[0].Body);
    }

    [Fact]
    public void A_refusal_without_errors_quotes_the_body_and_a_failed_request_names_the_call()
    {
        Hcp((m, p) => (500, """{"oops":1}"""));
        Assert.Equal("""HCP 500 on GET /runs/r: {"oops":1}""", Assert.Throws<FactoryException>(() => Sut.Run("r")).Message);

        _http.Reply = _ => throw new HttpRequestException("refused");
        Assert.Equal("HCP GET /runs/r: refused", Assert.Throws<FactoryException>(() => Sut.Run("r")).Message);
    }

    [Fact]
    public void A_log_url_that_cannot_be_read_is_a_refusal()
    {
        Hcp((m, p) => p switch
        {
            "/api/v2/workspaces/ws-1/runs?page%5Bsize%5D=1" => (200, """{"data":[{"id":"run-1","attributes":{"status":"errored"},"relationships":{"plan":{"data":{"id":"plan-1"}}}}]}"""),
            _ => (200, """{"data":{"attributes":{"log-read-url":"http://logs/plan-1"}}}"""),
        });
        var inner = _http.Reply!;
        _http.Reply = r => r.RequestUri!.Host == "logs" ? throw new HttpRequestException("gone") : inner(r);

        Assert.Equal("HCP GET http://logs/plan-1: gone", Assert.Throws<FactoryException>(() => Sut.RunLog("ws-1")).Message);
    }

    [Fact]
    public void A_POST_with_no_body_still_says_it_is_JSON_API_as_HCP_requires()
    {
        string? type = null;
        _env.Get("HCP_TOKEN").Returns("tok");
        _env.Get("HCP_API_URL").Returns("http://hcp/");
        _http.Reply = r =>
        {
            type = r.Content?.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };

        Sut.SafeDelete("ws-1");

        // HCP answers 415 "content-type must be application/vnd.api+json" otherwise (spec 14's live canary teardown).
        Assert.Equal("application/vnd.api+json", type);
    }
}