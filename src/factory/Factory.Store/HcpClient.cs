using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>hcp.ps1's Invoke-Hcp and its helpers over <see cref="HttpClient"/>, with HCP_ORG, HCP_TOKEN and HCP_API_URL from .env.</summary>
public sealed class HcpClient(HttpClient http, IDotEnv dotEnv, ISecrets secrets) : IHcpClient
{
    private static readonly int[] Ok = [200, 201, 202, 204];

    private string Org => dotEnv.Get("HCP_ORG") ?? "";

    public IReadOnlyList<HcpVar> VarsetVars()
    {
        if (dotEnv.Get("HCP_AZURE_VARSET") is not { } varset) return [];
        var body = Send(HttpMethod.Get, $"/varsets/{varset}/relationships/vars");
        return [.. Data(body).Select(d => d?["attributes"]).OfType<JsonObject>().Select(a => new HcpVar(
            PsJsonReader.Text(a["category"]), PsJsonReader.Text(a["key"]), PsJsonReader.Str(a["value"]), PsJsonReader.Truthy(a["sensitive"])))];
    }

    public string EnsureProject(string name)
    {
        var found = Data(Send(HttpMethod.Get, $"/organizations/{Org}/projects?filter%5Bnames%5D={Uri.EscapeDataString(name)}"))
            .FirstOrDefault(p => PsJsonReader.Is(PsJsonReader.Str(p?["attributes"]?["name"]), name));
        var id = found is not null ? PsJsonReader.Text(found["id"])
            : PsJsonReader.Text(Send(HttpMethod.Post, $"/organizations/{Org}/projects",
                new JsonObject { ["data"] = new JsonObject { ["type"] = "projects", ["attributes"] = new JsonObject { ["name"] = name } } })?["data"]?["id"]);
        if (dotEnv.Get("HCP_AZURE_VARSET") is { } varset)
            Send(HttpMethod.Post, $"/varsets/{varset}/relationships/projects",
                new JsonObject { ["data"] = new JsonArray(new JsonObject { ["type"] = "projects", ["id"] = id }) });
        return id;
    }

    public string EnsureWorkspace(string name, string projectId, string? autoDestroyAt = null)
    {
        var ws = Send(HttpMethod.Get, $"/organizations/{Org}/workspaces/{name}", allowNotFound: true);
        if (ws is null)
        {
            var attrs = new JsonObject { ["name"] = name, ["execution-mode"] = "remote" };
            if (!string.IsNullOrEmpty(autoDestroyAt)) attrs["auto-destroy-at"] = autoDestroyAt;
            return PsJsonReader.Text(Send(HttpMethod.Post, $"/organizations/{Org}/workspaces", new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["type"] = "workspaces", ["attributes"] = attrs,
                    ["relationships"] = new JsonObject { ["project"] = new JsonObject { ["data"] = new JsonObject { ["type"] = "projects", ["id"] = projectId } } },
                },
            })?["data"]?["id"]);
        }
        var id = PsJsonReader.Text(ws["data"]?["id"]);
        if (!string.IsNullOrEmpty(autoDestroyAt))
            Send(HttpMethod.Patch, $"/workspaces/{id}", new JsonObject
            {
                ["data"] = new JsonObject { ["type"] = "workspaces", ["attributes"] = new JsonObject { ["auto-destroy-at"] = autoDestroyAt } },
            });
        return id;
    }

    public string? WorkspaceId(string name) =>
        Send(HttpMethod.Get, $"/organizations/{Org}/workspaces/{name}", allowNotFound: true) is { } ws ? PsJsonReader.Str(ws["data"]?["id"]) : null;

    public IReadOnlyDictionary<string, string?> Outputs(string workspaceId, IReadOnlyCollection<string> sensitive)
    {
        var outs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in Data(Send(HttpMethod.Get, $"/workspaces/{workspaceId}/current-state-version-outputs")).OfType<JsonObject>())
        {
            var name = PsJsonReader.Text(o["attributes"]?["name"]);
            outs[name] = !PsJsonReader.Truthy(o["attributes"]?["sensitive"]) ? PsJsonReader.Str(o["attributes"]?["value"])
                : sensitive.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? PsJsonReader.Str(Send(HttpMethod.Get, $"/state-version-outputs/{PsJsonReader.Text(o["id"])}")?["data"]?["attributes"]?["value"])
                    : null;
        }
        return outs;
    }

    public string RunLog(string workspaceId)
    {
        if (Data(Send(HttpMethod.Get, $"/workspaces/{workspaceId}/runs?page%5Bsize%5D=1")).FirstOrDefault() is not JsonObject run) return "(no HCP run)";
        var text = new StringBuilder($"run {PsJsonReader.Text(run["id"])}: {PsJsonReader.Text(run["attributes"]?["status"])}");
        foreach (var (part, rel) in new[] { ("plans", "plan"), ("applies", "apply") })
        {
            if (PsJsonReader.Str(run["relationships"]?[rel]?["data"]?["id"]) is not { Length: > 0 } partId) continue;
            var url = PsJsonReader.Str(Send(HttpMethod.Get, $"/{part}/{partId}", allowNotFound: true)?["data"]?["attributes"]?["log-read-url"]);
            if (!string.IsNullOrEmpty(url)) text.Append($"\n--- {part}\n").Append(ReadLog(url));
        }
        return text.ToString();
    }

    public HcpRun QueueDestroy(string workspaceId) => ToRun(Send(HttpMethod.Post, "/runs", new JsonObject
    {
        ["data"] = new JsonObject
        {
            ["type"] = "runs",
            ["attributes"] = new JsonObject { ["is-destroy"] = true, ["auto-apply"] = true, ["message"] = "factory: tear down the preview" },
            ["relationships"] = new JsonObject { ["workspace"] = new JsonObject { ["data"] = new JsonObject { ["type"] = "workspaces", ["id"] = workspaceId } } },
        },
    }));

    public HcpRun Run(string runId) => ToRun(Send(HttpMethod.Get, $"/runs/{runId}"));

    public void SafeDelete(string workspaceId) => Send(HttpMethod.Post, $"/workspaces/{workspaceId}/actions/safe-delete");

    private static HcpRun ToRun(JsonNode? body) =>
        new(PsJsonReader.Text(body?["data"]?["id"]), PsJsonReader.Text(body?["data"]?["attributes"]?["status"]));

    private static JsonArray Data(JsonNode? body) => body?["data"] as JsonArray ?? [];

    /// <summary>A log-read-url is pre-signed: fetched with no token, its text whatever the status (Invoke-RestMethod -SkipHttpErrorCheck).</summary>
    private string ReadLog(string url)
    {
        try
        {
            using var res = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            return res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (HttpRequestException e) { throw new FactoryException($"HCP GET {url}: {e.Message}"); }
    }

    /// <summary>Invoke-Hcp: the parsed body (null for 204, or for 404 when allowed); any other status throws.</summary>
    private JsonNode? Send(HttpMethod method, string path, JsonNode? body = null, bool allowNotFound = false)
    {
        var token = secrets.Get(SecretNames.HcpToken) ?? throw new FactoryException("HCP_TOKEN is not set in .env");
        var api = (dotEnv.Get("HCP_API_URL") ?? "https://app.terraform.io").TrimEnd('/');
        using var req = new HttpRequestMessage(method, $"{api}/api/v2{path}");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            req.Content.Headers.ContentType = new("application/vnd.api+json");
        }
        HttpResponseMessage res;
        try { res = http.Send(req); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new FactoryException($"HCP {method.Method} {path}: {e.Message}"); }
        using (res)
        {
            using var s = res.Content.ReadAsStream();
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(s); } catch (JsonException) { parsed = null; }
            var status = (int)res.StatusCode;
            if (allowNotFound && status == 404) return null;
            if (Ok.Contains(status)) return parsed;
            var why = parsed?["errors"] is JsonArray errors
                ? string.Join("; ", errors.Select(e => PsJsonReader.Str(e?["detail"]) ?? PsJsonReader.Text(e?["title"])))
                : parsed?.ToJsonString() ?? "";
            throw new FactoryException($"HCP {status} on {method.Method} {path}: {why}");
        }
    }
}
