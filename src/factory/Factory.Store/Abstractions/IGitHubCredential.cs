namespace Factory.Store.Abstractions;

/// <summary>
/// What the factory signs in to GitHub with (contract, "Targets" and "Secrets, init and doctor"): the GitHub App's
/// tokens, the only GitHub credential.
/// </summary>
public interface IGitHubCredential
{
    /// <summary>
    /// The installation token for calls on <paramref name="owner"/>'s repos (null: GITHUB_OWNER's). Null when no App is
    /// set up; refuses (a FactoryException) when the App has no installation there.
    /// </summary>
    string? Token(string? owner = null);

    /// <summary>The App's user token, which acts as the human (for creating a repo), refreshed as needed. Null when there is none.</summary>
    string? UserToken();

    /// <summary>Why there is no token, as a refusal reads it.</summary>
    string Missing { get; }
}
