using Factory.Core;

namespace Factory.Store.Abstractions;

/// <summary>github.ps1: the GitHub REST API and authenticated git, signed in with an <see cref="IGitHubCredential"/>.</summary>
public interface IGitHub
{
    /// <summary>Invoke-GitHub: never throws; a missing credential or a failed request is status 0.</summary>
    GitHubResponse Send(HttpMethod method, string path, System.Text.Json.Nodes.JsonNode? body = null);

    /// <summary>Get-TargetState. Refuses with GitHub's error (<see cref="GitHubErrors.Text"/>).</summary>
    RepoState Lookup(string target);

    /// <summary>Get-OwnedRepos: the credential's user's own repos as owner/name, newest push first, one page of 100.</summary>
    IReadOnlyList<string> OwnedRepos();

    /// <summary>New-GitHubRepo: creates owner/name private and empty, under the credential's user if it is the owner, else in that org. Null, or why not.</summary>
    string? CreateRepo(string target);

    /// <summary>Get-OpenPr: the open PR from <paramref name="branch"/>, or null.</summary>
    PullRequest? OpenPr(string target, string branch);

    /// <summary>Get-Pr: PR <paramref name="number"/> as GitHub returns it (<c>merged</c>, <c>merge_commit_sha</c>, <c>head.sha</c>), or null.</summary>
    System.Text.Json.Nodes.JsonObject? Pr(string target, int number);

    /// <summary>&lt;FACTORY_GIT_BASE, else https://github.com&gt;/owner/name.git</summary>
    string CloneUrl(string target);

    /// <summary>
    /// Invoke-GitAuth's <c>-c</c> options: the token (for <paramref name="owner"/>'s repos; null: GITHUB_OWNER's) as a one-command
    /// header, never in a remote URL or .git/config.
    /// </summary>
    IReadOnlyList<string> GitAuth(string? owner = null);

    /// <summary>GET /user with the user token (the App's): the human's login. Refuses with GitHub's error.</summary>
    string UserLogin();
}
