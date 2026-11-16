using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// state.ps1's Get-StateToken: FACTORY_STATE_TOKEN (tests), else the managed identity (AZURE_CLIENT_ID names a user-assigned one), else
/// <c>az account get-access-token</c> (or FACTORY_AZ_CMD), cached until five minutes before expiry in
/// &lt;local dir&gt;/storage-token.json, never under FACTORY_HOME, so the next process need not ask az again.
/// </summary>
public sealed class StateToken(IOptions<StateOptions> options, IFileStore files, IProcessRunner processes, HttpClient http, TimeProvider clock) : IStateToken
{
    private const string Storage = "https://storage.azure.com";

    /// <summary>
    /// az's launcher: a batch file on Windows, which runs only by its full name (started as plain az.cmd it prints
    /// "Failed to load python executable."), so it is looked up on PATH; az elsewhere.
    /// </summary>
    public static readonly string AzProgram = OperatingSystem.IsWindows() ? ToolPath.Find("az.cmd", Environment.GetEnvironmentVariable("PATH")) : "az";

    private readonly Dictionary<string, (string Token, long ExpiresOn)> _tokens = [];

    private StateOptions O => options.Value;

    public string Get() => !string.IsNullOrEmpty(O.Token) ? O.Token : Get(Storage);

    /// <summary>
    /// A token for another resource (Key Vault's), from the same chain but FACTORY_STATE_TOKEN, cached in
    /// &lt;local dir&gt;/&lt;the resource host's first label&gt;-token.json (storage-token.json for storage).
    /// </summary>
    public string Get(string resource)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (_tokens.TryGetValue(resource, out var t) && t.ExpiresOn > now + 300) return t.Token;
        if (!string.IsNullOrEmpty(O.IdentityEndpoint) && !string.IsNullOrEmpty(O.IdentityHeader)) return Remember(resource, ManagedIdentity(resource));
        var cache = Path.Combine(O.LocalDir ?? "", $"{new Uri(resource).Host.Split('.')[0]}-token.json");
        if (Cached(cache) is { } c && c.ExpiresOn > now + 300) return Remember(resource, c);
        var fresh = Az(resource);
        files.WriteAtomic(cache, Encoding.UTF8.GetBytes(new JsonObject { ["token"] = fresh.Token, ["expiresOn"] = fresh.ExpiresOn }.ToJsonString()));
        return Remember(resource, fresh);
    }

    private string Remember(string resource, (string Token, long ExpiresOn) t) { _tokens[resource] = t; return t.Token; }

    private (string Token, long ExpiresOn) ManagedIdentity(string resource)
    {
        var client = string.IsNullOrEmpty(O.ClientId) ? "" : $"&client_id={O.ClientId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{O.IdentityEndpoint}?resource={resource}&api-version=2019-08-01{client}");
        req.Headers.TryAddWithoutValidation("X-IDENTITY-HEADER", O.IdentityHeader);
        using var res = http.Send(req);
        var j = JsonNode.Parse(res.Content.ReadAsStream())!;
        return ((string)j["access_token"]!, long.Parse(j["expires_on"]!.ToString(), CultureInfo.InvariantCulture));
    }

    private (string Token, long ExpiresOn)? Cached(string path)
    {
        try
        {
            if (files.ReadBytes(path) is not { } bytes || JsonNode.Parse(bytes) is not JsonObject c) return null;
            return ((string)c["token"]!, long.Parse(c["expiresOn"]!.ToString(), CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException) { return null; }
    }

    private (string Token, long ExpiresOn) Az(string resource)
    {
        string[] args = ["account", "get-access-token", "--resource", resource, "--output", "json"];
        var r = string.IsNullOrEmpty(O.AzCmd)
            ? processes.Run(AzProgram, args, "")
            : processes.Run(CommandOverride.Parse("FACTORY_AZ_CMD", O.AzCmd)[0], [.. CommandOverride.Parse("FACTORY_AZ_CMD", O.AzCmd).Skip(1), .. args], "");
        var text = r.StdOut.Trim();
        if (r.ExitCode != 0 || text.Length == 0)
            throw new FactoryException($"state: az account get-access-token failed (is az logged in?): {(r.StdErr + r.StdOut).Trim()}");
        var j = JsonNode.Parse(text)!;
        var exp = j["expires_on"] is { } unix
            ? long.Parse(unix.ToString(), CultureInfo.InvariantCulture)
            : new DateTimeOffset(DateTime.Parse((string)j["expiresOn"]!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)).ToUnixTimeSeconds();
        return ((string)j["accessToken"]!, exp);
    }
}
