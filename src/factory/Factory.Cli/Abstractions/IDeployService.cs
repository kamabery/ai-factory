using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli.Abstractions;

/// <summary>
/// deploy.ps1: a job's ephemeral preview (contract, "Deploy"), and the steps the canary shares with it. The long steps run
/// with the board lock released, as the gate does, so callers re-fetch their job after <see cref="Attempt"/> and
/// <see cref="Destroy"/>.
/// </summary>
public interface IDeployService
{
    /// <summary>
    /// Deploy steps 2-7 for the job in deploy (its record already says <c>applying</c>): push, then with the lock released
    /// GitHub and Azure preparation, HCP, the dispatch and its poll, the outputs and both smokes. Never throws: a failure
    /// is in the result's log. <paramref name="resume"/> takes the newest run of the pushed head even when it has finished
    /// (J-023); <paramref name="since"/> is sent only when the branch's deploy.yml declares it.
    /// </summary>
    DeployAttempt Attempt(IBoardSession session, Job job, bool resume, string? since);

    /// <summary>
    /// Destroys the job's preview, resources then workspace, with the lock released. Null, or why not. Uses the recorded
    /// workspaceId, so it needs HCP_TOKEN but not HCP_ORG.
    /// </summary>
    string? Destroy(IBoardSession session, Job job);

    /// <summary>Destroys a workspace's resources (a 422 "Configuration version is missing" has none, J-018), then deletes it. Null, or why not.</summary>
    string? RemovePreview(string workspaceId);

    /// <summary>Step 3: the target's <c>preview</c> environment, its variables and secret, and Azure's federated credential for it.</summary>
    void PrepareGitHub(string target);

    /// <summary>
    /// Step 5: dispatches deploy.yml on <paramref name="branch"/> and waits for that run, or for one of
    /// <paramref name="sha"/> that is still going (or, with <paramref name="resume"/>, the newest one); returns it, completed.
    /// <paramref name="progress"/> sees the run on each poll that finds it, the completed one last (the canary's phases).
    /// </summary>
    JsonObject DeployRun(string target, string environment, string branch, string? sha = null, bool resume = false, string? since = null,
        Action<JsonObject>? progress = null);

    /// <summary>The failed jobs' logs of a GitHub run, as text.</summary>
    string RunLog(string target, JsonObject run);

    /// <summary>Step 7: GETs /health and the front end until each answers 200. Adds one line per url; null, or the first failure.</summary>
    string? Smoke(JsonObject? urls, List<string> lines);

    /// <summary>
    /// Step 7a: signs in as the QA user when the outputs have one and there is a front end, retrying AADSTS9002326 (Entra
    /// replicating a new app, about 90 s on J-025) up to five tries. Adds the steps, never the password; null, or why not.
    /// </summary>
    string? SigninSmoke(IReadOnlyDictionary<string, string?> outputs, JsonObject? urls, string shots, List<string> lines);
}
