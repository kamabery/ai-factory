using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// The GitHub App's own calls over <see cref="HttpClient"/>: the API at GITHUB_API_URL (else api.github.com), the
/// OAuth endpoints at GITHUB_WEB_URL (else github.com). Tests point both at Factory.TestHost's GitHub stub.
/// </summary>
public sealed class GitHubAppClient(HttpClient http, IDotEnv dotEnv) : IGitHubApp
{
    private string Api => (dotEnv.Get("GITHUB_API_URL") ?? "https://api.github.com").TrimEnd('/');
    private string Web => (dotEnv.Get("GITHUB_WEB_URL") ?? "https://github.com").TrimEnd('/');

    public JsonObject App(string jwt) => Call(HttpMethod.Get, $"{Api}/app", jwt, null, "GET /app") as JsonObject ?? [];

    public IReadOnlyList<AppInstallation> Installations(string jwt)
    {
        var list = Call(HttpMethod.Get, $"{Api}/app/installations?per_page=100", jwt, null, "GET /app/installations") as JsonArray ?? [];
        return [.. list.OfType<JsonObject>().Select(i => new AppInstallation(
            i["id"]!.GetValue<long>(), PsJsonReader.Text(i["account"]?["login"]), PsJsonReader.Text(i["repository_selection"]),
            (i["permissions"] as JsonObject ?? []).ToDictionary(p => p.Key, p => PsJsonReader.Text(p.Value), StringComparer.Ordinal)))];
    }

    public AppToken InstallationToken(string jwt, long installationId)
    {
        var o = Call(HttpMethod.Post, $"{Api}/app/installations/{installationId}/access_tokens", jwt, null, "minting an installation token") as JsonObject ?? [];
        return new AppToken(PsJsonReader.Text(o["token"]), DateTimeOffset.Parse(PsJsonReader.Text(o["expires_at"]), CultureInfo.InvariantCulture));
    }

    public AppRegistration ConvertManifest(string code)
    {
        var o = Call(HttpMethod.Post, $"{Api}/app-manifests/{Uri.EscapeDataString(code)}/conversions", null, null, "converting the App manifest") as JsonObject ?? [];
        return new AppRegistration(PsJsonReader.Text(o["id"]), PsJsonReader.Text(o["slug"]), PsJsonReader.Text(o["client_id"]),
            PsJsonReader.Text(o["client_secret"]), PsJsonReader.Text(o["pem"]), PsJsonReader.Text(o["owner"]?["login"]));
    }

    public UserTokens ExchangeCode(string clientId, string clientSecret, string code, string redirectUri) =>
        OAuth(new() { ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code"] = code, ["redirect_uri"] = redirectUri });

    public UserTokens Refresh(string clientId, string clientSecret, string refreshToken) =>
        OAuth(new() { ["client_id"] = clientId, ["client_secret"] = clientSecret, ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken });

    // GitHub's OAuth endpoint answers 200 with an "error" field when it refuses.
    private UserTokens OAuth(Dictionary<string, string> form)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Web}/login/oauth/access_token") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var o = Send(req, "the GitHub sign-in") as JsonObject ?? [];
        if (PsJsonReader.Str(o["error"]) is { } error)
            throw new FactoryException($"the GitHub sign-in failed: {error}: {PsJsonReader.Str(o["error_description"]) ?? ""}".TrimEnd(' ', ':'));
        var seconds = o["expires_in"] is { } s ? (long?)s.GetValue<long>() : null;
        return new UserTokens(PsJsonReader.Text(o["access_token"]), PsJsonReader.Str(o["refresh_token"]),
            seconds is { } n ? DateTimeOffset.UtcNow.AddSeconds(n) : null);
    }

    private JsonNode? Call(HttpMethod method, string url, string? jwt, JsonNode? body, string what)
    {
        using var req = new HttpRequestMessage(method, url);
        if (jwt is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return Send(req, what);
    }

    private JsonNode? Send(HttpRequestMessage req, string what)
    {
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ai-factory", "1.0"));
        HttpResponseMessage res;
        try { res = http.Send(req); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new FactoryException($"GitHub ({what}): {e.Message}"); }
        using (res)
        {
            var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JsonNode? node = null;
            try { node = text.Length == 0 ? null : JsonNode.Parse(text); } catch (JsonException) { }
            if (res.IsSuccessStatusCode) return node;
            var message = PsJsonReader.Str(node?["message"]) ?? text;
            throw new FactoryException($"GitHub {(int)res.StatusCode} ({what}): {message}");
        }
    }
}
