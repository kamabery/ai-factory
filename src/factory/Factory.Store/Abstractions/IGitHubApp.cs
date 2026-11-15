namespace Factory.Store.Abstractions;

/// <summary>
/// The GitHub App's own calls (contract, "Secrets, init and doctor"): as the App (a JWT), with no auth (the manifest
/// conversion), and the user-token exchange on the web host. Each refuses with GitHub's message.
/// </summary>
public interface IGitHubApp
{
    /// <summary>GET /app: the App as GitHub has it.</summary>
    System.Text.Json.Nodes.JsonObject App(string jwt);

    /// <summary>GET /app/installations.</summary>
    IReadOnlyList<AppInstallation> Installations(string jwt);

    /// <summary>POST /app/installations/&lt;id&gt;/access_tokens: a one-hour installation token.</summary>
    AppToken InstallationToken(string jwt, long installationId);

    /// <summary>POST /app-manifests/&lt;code&gt;/conversions.</summary>
    AppRegistration ConvertManifest(string code);

    /// <summary>POST &lt;web&gt;/login/oauth/access_token with the code the human's sign-in returned.</summary>
    UserTokens ExchangeCode(string clientId, string clientSecret, string code, string redirectUri);

    /// <summary>The same, grant_type=refresh_token.</summary>
    UserTokens Refresh(string clientId, string clientSecret, string refreshToken);
}
