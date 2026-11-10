using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>hcp.ps1's Invoke-Hcp over <see cref="HttpClient"/>, with HCP_TOKEN and HCP_API_URL from .env.</summary>
public sealed class HcpClient(HttpClient http, IDotEnv dotEnv) : IHcpClient
{
    public IReadOnlyList<HcpVar> VarsetVars()
    {
        if (dotEnv.Get("HCP_AZURE_VARSET") is not { } varset) return [];
        var body = Get($"/varsets/{varset}/relationships/vars");
        return [.. (body?["data"] as JsonArray ?? []).Select(d => d?["attributes"]).OfType<JsonObject>().Select(a => new HcpVar(
            PsJsonReader.Text(a["category"]), PsJsonReader.Text(a["key"]), PsJsonReader.Str(a["value"]), PsJsonReader.Truthy(a["sensitive"])))];
    }

    private JsonNode? Get(string path)
    {
        var token = dotEnv.Get("HCP_TOKEN") ?? throw new FactoryException("HCP_TOKEN is not set in .env");
        var api = (dotEnv.Get("HCP_API_URL") ?? "https://app.terraform.io").TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{api}/api/v2{path}");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        HttpResponseMessage res;
        try { res = http.Send(req); }
        catch (HttpRequestException e) { throw new FactoryException($"HCP GET {path}: {e.Message}"); }
        using (res)
        {
            using var s = res.Content.ReadAsStream();
            JsonNode? body;
            try { body = JsonNode.Parse(s); } catch (JsonException) { body = null; }
            var status = (int)res.StatusCode;
            if (status is 200 or 201 or 202 or 204) return body;
            var why = body?["errors"] is JsonArray errors
                ? string.Join("; ", errors.Select(e => PsJsonReader.Str(e?["detail"]) ?? PsJsonReader.Text(e?["title"])))
                : body?.ToJsonString() ?? "";
            throw new FactoryException($"HCP {status} on GET {path}: {why}");
        }
    }
}
