using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>The GitHub PAT: GITHUB_TOKEN (the environment, the secret store or .env), for every owner and as the user.</summary>
public sealed class PatCredential(ISecrets secrets) : IGitHubCredential
{
    public string? Token(string? owner = null) => secrets.Get(SecretNames.Pat);

    public string? UserToken() => Token();

    public bool IsApp => false;

    public string Missing => "GITHUB_TOKEN is not set in .env";
}
