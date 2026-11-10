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
        if (credential.Token() is not { } token) return new GitHubResponse(0, JsonValue.Create(credential.Missing), null);
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
        var r = Send(HttpMethod.Get, "/user/repos?affiliation=owner&sort=pushed&direction=desc&per_page=100");
        if (r.Status != 200) throw new FactoryException(GitHubErrors.Text(r));
        return r.Body is JsonArray a ? [.. a.Select(x => PsJsonReader.Text(x?["full_name"]))] : [];
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
