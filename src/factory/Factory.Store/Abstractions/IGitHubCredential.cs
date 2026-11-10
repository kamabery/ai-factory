namespace Factory.Store.Abstractions;

/// <summary>
/// What the factory signs in to GitHub with. Today the GitHub PAT in .env (<see cref="PatCredential"/>); roadmap
/// spec 14 swaps in GitHub App installation tokens here, without touching callers.
/// </summary>
public interface IGitHubCredential
{
    /// <summary>The token for the API's bearer header and git's <c>x-access-token</c>; null when there is none.</summary>
    string? Token();

    /// <summary>Why there is no token, as a refusal reads it.</summary>
    string Missing { get; }
}
