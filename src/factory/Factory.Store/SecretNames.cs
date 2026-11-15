namespace Factory.Store;

/// <summary>Every setting that is a secret (contract, "Secrets, init and doctor").</summary>
public static class SecretNames
{
    public const string AppId = "GITHUB_APP_ID";
    public const string AppSlug = "GITHUB_APP_SLUG";
    public const string AppClientId = "GITHUB_APP_CLIENT_ID";
    public const string AppClientSecret = "GITHUB_APP_CLIENT_SECRET";
    public const string AppPrivateKey = "GITHUB_APP_PRIVATE_KEY";
    public const string UserToken = "GITHUB_USER_TOKEN";
    public const string UserRefreshToken = "GITHUB_USER_REFRESH_TOKEN";
    public const string UserTokenExpires = "GITHUB_USER_TOKEN_EXPIRES";
    public const string HcpToken = "HCP_TOKEN";
    public const string HcpTeamToken = "HCP_TEAM_TOKEN";

    public static readonly IReadOnlyList<string> All =
        [AppId, AppSlug, AppClientId, AppClientSecret, AppPrivateKey, UserToken, UserRefreshToken, UserTokenExpires, HcpToken, HcpTeamToken];
}
