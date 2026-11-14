using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// One Messages request to a Foundry deployment (https://&lt;resource&gt;.services.ai.azure.com/anthropic/v1/messages), with an
/// Entra token from az for https://ai.azure.com: the same identity claude uses in cloud mode. Raw HTTP, because the
/// factory talks to Claude only through the claude CLI and this is a health probe, not a feature.
/// </summary>
public sealed class FoundryProbe(HttpClient http, IProcessRunner processes, IDotEnv dotEnv) : IFoundryProbe
{
    private string? _token;

    public string? Ping(string resource, string deployment)
    {
        string token;
        try { token = _token ??= Token(); }
        catch (FactoryException e) { return e.Message; }
        var url = dotEnv.Get("FACTORY_FOUNDRY_URL") is { } test ? $"{test.TrimEnd('/')}/anthropic/v1/messages"
            : $"https://{resource}.services.ai.azure.com/anthropic/v1/messages";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(new JsonObject
            {
                ["model"] = deployment, ["max_tokens"] = 1,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "ping" }),
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2026-10-21");
        try
        {
            using var res = http.Send(req);
            if (res.IsSuccessStatusCode) return null;
            var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string? message = null;
            try { message = PsJsonReader.Str(JsonNode.Parse(body)?["error"]?["message"]); } catch (System.Text.Json.JsonException) { }
            return $"{(int)res.StatusCode} {message ?? body}".Trim();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return e.Message; }
    }

    private string Token()
    {
        ProcessResult r;
        try { r = processes.Run(StateToken.AzProgram, ["account", "get-access-token", "--resource", "https://ai.azure.com", "--query", "accessToken", "-o", "tsv"], ""); }
        catch (System.ComponentModel.Win32Exception) { throw new FactoryException("az is not installed"); }
        if (r.ExitCode != 0 || r.StdOut.Trim().Length == 0) throw new FactoryException($"az has no token for Foundry (is az logged in?): {(r.StdErr + r.StdOut).Trim()}");
        return r.StdOut.Trim();
    }
}
