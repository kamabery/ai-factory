using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>The GitHub PAT: GITHUB_TOKEN (the environment, the secret store or .env).</summary>
public sealed class PatCredential(ISecrets secrets) : IGitHubCredential
{
    public string? Token() => secrets.Get(SecretNames.Pat);

    public string Missing => "GITHUB_TOKEN is not set in .env";
}
