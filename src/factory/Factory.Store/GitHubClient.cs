using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>github.ps1 over <see cref="HttpClient"/>, with GITHUB_API_URL and FACTORY_GIT_BASE from .env.</summary>
public sealed class GitHubClient(HttpClient http, IDotEnv dotEnv, IGitHubCredential credential) : IGitHub
{
    private string Api => (dotEnv.Get("GITHUB_API_URL") ?? "https://api.github.com").TrimEnd('/');
    private string GitBase => (dotEnv.Get("FACTORY_GIT_BASE") ?? "https://github.com").TrimEnd('/', '\\');

    public GitHubResponse Send(HttpMethod method, string path, JsonNode? body = null)
    {
        string? token;
        try { token = credential.Token(OwnerOf(path)); }
        catch (FactoryException e) { return new GitHubResponse(0, JsonValue.Create(e.Message), null); }
        return SendAs(token, method, path, body);
    }

    // The App's installation token is per account: the one a repo or org path names, else GITHUB_OWNER's.
    private static string? OwnerOf(string path) =>
        path.StartsWith("/repos/", StringComparison.Ordinal) || path.StartsWith("/orgs/", StringComparison.Ordinal)
            ? path.Split('/', 4, StringSplitOptions.None)[2].Split('?')[0] : null;

    private GitHubResponse SendAs(string? token, HttpMethod method, string path, JsonNode? body)
    {
        if (token is null) return new GitHubResponse(0, JsonValue.Create(credential.Missing), null);
        using var req = new HttpRequestMessage(method, $"{Api}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        // GitHub refuses a request with no User-Agent; Invoke-RestMethod always sent one.
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ai-factory", "1.0"));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage res;
        try { res = http.Send(req); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new GitHubResponse(0, JsonValue.Create(e.Message), null); }
        using (res)
        {
            var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var status = (int)res.StatusCode;
            var permission = status == 403 && res.Headers.TryGetValues("X-Accepted-GitHub-Permissions", out var p) ? string.Join(", ", p) : null;
            return new GitHubResponse(status, Parse(text), string.IsNullOrEmpty(permission) ? null : permission);
        }
    }

    public RepoState Lookup(string target)
    {
        var r = Send(HttpMethod.Get, $"/repos/{target}");
        if (r.Status == 404) return RepoState.Missing;
        if (r.Status != 200) throw new FactoryException(GitHubErrors.Text(r));
        var b = Send(HttpMethod.Get, $"/repos/{target}/branches/main");
        if (b.Status == 404) return RepoState.Empty;
        if (b.Status != 200) throw new FactoryException(GitHubErrors.Text(b));
        return RepoState.Existing;
    }

    public IReadOnlyList<string> OwnedRepos()
    {
        // An installation token has no user: the repos are the installation's, newest push first.
        var i = Send(HttpMethod.Get, "/installation/repositories?per_page=100");
        if (i.Status != 200) throw new FactoryException(GitHubErrors.Text(i));
        return [.. (i.Body?["repositories"] as JsonArray ?? []).OfType<JsonObject>()
            .OrderByDescending(x => PsJsonReader.Str(x["pushed_at"]), StringComparer.Ordinal).Select(x => PsJsonReader.Text(x["full_name"]))];
    }

    // Creating a repo acts as the human: the App's user token (an installation cannot create one on a user account).
    public string? CreateRepo(string target)
    {
        var parts = target.Split('/', 2);
        string? token;
        try { token = credential.UserToken(); }
        catch (FactoryException e) { return Refused(target, e.Message); }
        if (token is null) return Refused(target, NoUserToken);
        var me = SendAs(token, HttpMethod.Get, "/user", null);
        if (me.Status != 200) return Refused(target, GitHubErrors.Text(me));
        var path = PsJsonReader.Is(PsJsonReader.Str((me.Body as JsonObject)?["login"]), parts[0]) ? "/user/repos" : $"/orgs/{parts[0]}/repos";
        var r = SendAs(token, HttpMethod.Post, path, new JsonObject { ["name"] = parts[1], ["private"] = true, ["auto_init"] = false });
        return r.Status == 201 ? null : Refused(target, GitHubErrors.Text(r));
    }

    public string UserLogin()
    {
        var token = credential.UserToken() ?? throw new FactoryException(NoUserToken);
        var me = SendAs(token, HttpMethod.Get, "/user", null);
        if (me.Status != 200) throw new FactoryException(GitHubErrors.Text(me));
        return PsJsonReader.Text((me.Body as JsonObject)?["login"]);
    }

    private const string NoUserToken = "the GitHub App has no user token; run factory init";

    private static string Refused(string target, string why) =>
        $"GitHub refused to create {target} ({why}); create it, empty, then approve the job again";

    public PullRequest? OpenPr(string target, string branch)
    {
        var owner = target.Split('/', 2)[0];
        var r = Send(HttpMethod.Get, $"/repos/{target}/pulls?state=open&head={owner}:{branch}");
        if (r.Status != 200) return null;
        var pr = r.Body switch { JsonArray a => a.FirstOrDefault() as JsonObject, JsonObject o => o, _ => null };
        return pr is null ? null : new PullRequest(PsJsonReader.Int(pr["number"]), PsJsonReader.Text(pr["html_url"]));
    }

    public JsonObject? Pr(string target, int number)
    {
        var r = Send(HttpMethod.Get, $"/repos/{target}/pulls/{number}");
        return r.Status == 200 ? r.Body as JsonObject : null;
    }

    public string CloneUrl(string target) => $"{GitBase}/{target}.git";

    public IReadOnlyList<string> GitAuth()
    {
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{credential.Token()}"));
        return ["-c", "credential.helper=", "-c", $"http.extraHeader=Authorization: Basic {basic}"];
    }

    // Invoke-RestMethod's body: JSON when it parses, else the text itself; nothing for an empty answer.
    private static JsonNode? Parse(string text)
    {
        if (text.Length == 0) return null;
        try { return JsonNode.Parse(text); } catch (JsonException) { return JsonValue.Create(text); }
    }
}
