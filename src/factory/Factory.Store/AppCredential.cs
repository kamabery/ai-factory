using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// The GitHub App's tokens (contract, "Secrets, init and doctor"): installation tokens per owner, cached in the local
/// dir until five minutes before they expire, and the user token, refreshed when it is that close to expiring.
/// </summary>
public sealed class AppCredential(
    ISecrets secrets, ISecretStore store, IDotEnv dotEnv, IGitHubApp app, IFileStore files, IOptions<StateOptions> state, TimeProvider clock)
    : IGitHubCredential
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, AppToken> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public string Missing => "no GitHub App is set up; run factory init";

    private bool NoApp => string.IsNullOrEmpty(secrets.Get(SecretNames.AppId));

    public string? Token(string? owner = null)
    {
        if (NoApp) return null;
        owner ??= dotEnv.Get("GITHUB_OWNER") ?? throw new FactoryException("GITHUB_OWNER is not set in .env; run factory init");
        var now = clock.GetUtcNow();
        if (_tokens.TryGetValue(owner, out var t) && t.ExpiresAt - Margin > now) return t.Token;
        var cache = Path.Combine(state.Value.LocalDir ?? "", $"github-token-{owner.ToLowerInvariant()}.json");
        if (Cached(cache) is { } c && c.ExpiresAt - Margin > now) return (_tokens[owner] = c).Token;

        var jwt = GitHubJwt.Create(Need(SecretNames.AppId), Need(SecretNames.AppPrivateKey), now);
        var installation = app.Installations(jwt).FirstOrDefault(i => string.Equals(i.Account, owner, StringComparison.OrdinalIgnoreCase))
            ?? throw new FactoryException($"the GitHub App is not installed on {owner}; install it (factory init), or check GITHUB_OWNER");
        var fresh = app.InstallationToken(jwt, installation.Id);
        files.WriteAtomic(cache, Encoding.UTF8.GetBytes(new JsonObject { ["token"] = fresh.Token, ["expiresAt"] = fresh.ExpiresAt.ToString("o") }.ToJsonString()));
        return (_tokens[owner] = fresh).Token;
    }

    public string? UserToken()
    {
        if (NoApp) return null;
        var token = secrets.Get(SecretNames.UserToken);
        if (token is null) return null;
        var expires = secrets.Get(SecretNames.UserTokenExpires) is { } e ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(e, CultureInfo.InvariantCulture)) : (DateTimeOffset?)null;
        if (expires is null || expires - Margin > clock.GetUtcNow()) return token;
        var refresh = secrets.Get(SecretNames.UserRefreshToken)
            ?? throw new FactoryException("the GitHub App's user token expired and there is no refresh token; run factory init");
        var next = app.Refresh(Need(SecretNames.AppClientId), Need(SecretNames.AppClientSecret), refresh);
        Keep(store, next);
        return next.Token;
    }

    /// <summary>Stores a user token, its refresh token and its expiry, as init and a refresh leave them.</summary>
    public static void Keep(ISecretStore store, UserTokens t)
    {
        store.Set(SecretNames.UserToken, t.Token);
        if (t.Refresh is not null) store.Set(SecretNames.UserRefreshToken, t.Refresh);
        if (t.ExpiresAt is { } at) store.Set(SecretNames.UserTokenExpires, at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        else store.Delete(SecretNames.UserTokenExpires);
    }

    private string Need(string name) => secrets.Get(name) ?? throw new FactoryException($"{name} is not in the secret store; run factory init");

    private AppToken? Cached(string path)
    {
        try
        {
            if (files.ReadText(path) is not { } text || JsonNode.Parse(text) is not JsonObject o) return null;
            return new AppToken((string)o["token"]!, DateTimeOffset.Parse((string)o["expiresAt"]!, CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException or NullReferenceException) { return null; }
    }
}
