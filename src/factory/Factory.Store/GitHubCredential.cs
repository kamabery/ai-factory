using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>The GitHub App once GITHUB_APP_ID is set (factory init), else the GitHub PAT, until spec 15 drops it.</summary>
public sealed class GitHubCredential(ISecrets secrets, PatCredential pat, AppCredential app) : IGitHubCredential
{
    private IGitHubCredential Active => string.IsNullOrEmpty(secrets.Get(SecretNames.AppId)) ? pat : app;

    public string? Token(string? owner = null) => Active.Token(owner);

    public string? UserToken() => Active.UserToken();

    public bool IsApp => Active.IsApp;

    public string Missing => Active.Missing;
}
