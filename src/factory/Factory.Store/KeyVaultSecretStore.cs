using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// A deployed factory's store (contract, "Secrets, init and doctor"): Key Vault's REST API with an Entra token. A
/// secret is named with each <c>_</c> as <c>-</c>. Deleting purges too, so the name can be set again (the doctor
/// probe); a set that meets a soft-deleted secret purges it and sets again.
/// </summary>
public sealed class KeyVaultSecretStore(HttpClient http, IStateToken token, string vault) : ISecretStore
{
    private const string Resource = "https://vault.azure.net";
    private const string Api = "api-version=7.4";
    private readonly string _vault = vault.TrimEnd('/');

    public string Name => $"Key Vault {new Uri(_vault).Host}";

    public string? Get(string name)
    {
        var (status, body) = Send(HttpMethod.Get, "secrets", name, null);
        if (status == HttpStatusCode.NotFound) return null;
        Ok(status, body, HttpMethod.Get, name);
        return (string?)JsonNode.Parse(body)?["value"];
    }

    public void Set(string name, string value)
    {
        var json = new JsonObject { ["value"] = value }.ToJsonString();
        var (status, body) = Send(HttpMethod.Put, "secrets", name, json);
        if (status == HttpStatusCode.Conflict)
        {
            Send(HttpMethod.Delete, "deletedsecrets", name, null);
            (status, body) = Send(HttpMethod.Put, "secrets", name, json);
        }
        Ok(status, body, HttpMethod.Put, name);
    }

    public void Delete(string name)
    {
        var (status, body) = Send(HttpMethod.Delete, "secrets", name, null);
        if (status != HttpStatusCode.NotFound) Ok(status, body, HttpMethod.Delete, name);
        Send(HttpMethod.Delete, "deletedsecrets", name, null);
    }

    private static string Key(string name) => name.Replace('_', '-');

    private (HttpStatusCode Status, string Body) Send(HttpMethod method, string collection, string name, string? json)
    {
        using var req = new HttpRequestMessage(method, $"{_vault}/{collection}/{Key(name)}?{Api}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Get(Resource));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = http.Send(req);
        using var reader = new StreamReader(res.Content.ReadAsStream());
        return (res.StatusCode, reader.ReadToEnd());
    }

    private static void Ok(HttpStatusCode status, string body, HttpMethod method, string name)
    {
        if ((int)status is >= 200 and < 300) return;
        string? message = null;
        try { message = (string?)JsonNode.Parse(body)?["error"]?["message"]; }
        catch (JsonException) { }
        throw new FactoryException($"Key Vault {(int)status} on {method} {Key(name)}: {message ?? body.Trim()}");
    }
}
