namespace Factory.Store.Abstractions;

/// <summary>
/// What the factory signs in to GitHub with (contract, "Targets" and "Secrets, init and doctor"): the GitHub App's
/// tokens, or with no App the GitHub PAT, without callers telling them apart.
/// </summary>
public interface IGitHubCredential
{
    /// <summary>
    /// The token for calls on <paramref name="owner"/>'s repos (null: GITHUB_OWNER's): the App's installation token for
    /// that account, or the PAT. Null when there is none; refuses (a FactoryException) when the App has no installation there.
    /// </summary>
    string? Token(string? owner = null);

    /// <summary>The token that acts as the human, for creating a repo: the App's user token, refreshed as needed, or the PAT.</summary>
    string? UserToken();

    /// <summary>Whether this is the GitHub App, not the PAT fallback.</summary>
    bool IsApp { get; }

    /// <summary>Why there is no token, as a refusal reads it.</summary>
    string Missing { get; }
}
