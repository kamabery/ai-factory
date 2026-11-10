using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>The GitHub PAT: GITHUB_TOKEN in .env.</summary>
public sealed class PatCredential(IDotEnv dotEnv) : IGitHubCredential
{
    public string? Token() => dotEnv.Get("GITHUB_TOKEN");

    public string Missing => "GITHUB_TOKEN is not set in .env";
}
