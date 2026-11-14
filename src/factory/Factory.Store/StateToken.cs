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
/// state.ps1's Get-StateToken: FACTORY_STATE_TOKEN (tests), else the managed identity, else
/// <c>az account get-access-token</c> (or FACTORY_AZ_CMD), cached until five minutes before expiry in
/// &lt;local dir&gt;/storage-token.json, never under FACTORY_HOME, so the next process need not ask az again.
/// </summary>
public sealed class StateToken(IOptions<StateOptions> options, IFileStore files, IProcessRunner processes, HttpClient http, TimeProvider clock) : IStateToken
{
    private const string Resource = "https://storage.azure.com";

    /// <summary>
    /// az's launcher: a batch file on Windows, which runs only by its full name (started as plain az.cmd it prints
    /// "Failed to load python executable."), so it is looked up on PATH; az elsewhere.
    /// </summary>
    public static readonly string AzProgram = OperatingSystem.IsWindows() ? ToolPath.Find("az.cmd", Environment.GetEnvironmentVariable("PATH")) : "az";

    private (string Token, long ExpiresOn)? _token;

    private StateOptions O => options.Value;

    public string Get()
    {
        if (!string.IsNullOrEmpty(O.Token)) return O.Token;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (_token is { } t && t.ExpiresOn > now + 300) return t.Token;
        if (!string.IsNullOrEmpty(O.IdentityEndpoint) && !string.IsNullOrEmpty(O.IdentityHeader)) return Remember(ManagedIdentity());
        var cache = Path.Combine(O.LocalDir ?? "", "storage-token.json");
        if (Cached(cache) is { } c && c.ExpiresOn > now + 300) return Remember(c);
        var fresh = Az();
        files.WriteAtomic(cache, Encoding.UTF8.GetBytes(new JsonObject { ["token"] = fresh.Token, ["expiresOn"] = fresh.ExpiresOn }.ToJsonString()));
        return Remember(fresh);
    }

    private string Remember((string Token, long ExpiresOn) t) { _token = t; return t.Token; }

    private (string Token, long ExpiresOn) ManagedIdentity()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{O.IdentityEndpoint}?resource={Resource}&api-version=2019-08-01");
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

    private (string Token, long ExpiresOn) Az()
    {
        string[] args = ["account", "get-access-token", "--resource", Resource, "--output", "json"];
        var r = string.IsNullOrEmpty(O.AzCmd)
            ? processes.Run(AzProgram, args, "")
            : processes.Run("pwsh", ["-NoProfile", "-File", O.AzCmd, .. args], "");
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
