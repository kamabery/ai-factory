using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli.Abstractions;

/// <summary>
/// factory.ps1's git helpers (contract, "Gate and merge"): a job's clone, its PR, its gate and what a merge completes.
/// Helpers return null or why not, as factory.ps1's did, so each command chooses a refusal or a flag.
/// </summary>
public interface IJobWork
{
    /// <summary>Ensure-Clone: clones the target into the job's work dir (creating the repo if need be) and checks out factory/&lt;id&gt;.</summary>
    string? EnsureClone(JsonObject board, string id);

    /// <summary>Invoke-GitAuth fetch: <c>git fetch -q origin [refs]</c> in <paramref name="workDir"/>, with the credential.</summary>
    GitResult Fetch(string workDir, params string[] refs);

    /// <summary>Publish-Job: pushes factory/&lt;id&gt; and opens its PR if it has none; records the PR. The PR url, or why not.</summary>
    (string? Url, string? Error) Publish(IBoardSession session, Job job);

    /// <summary>Gate-Commands: FACTORY_GATE_CMDS, else the spec's <c>## Gate</c>.</summary>
    IReadOnlyList<GateCommand> GateCommands(Job job);

    /// <summary>
    /// Invoke-Gate: runs the gate in the work dir with the board lock released and records the exit codes and sha on the
    /// job. Null when it ran (green or red), else why not. Callers re-fetch the job.
    /// </summary>
    string? Gate(IBoardSession session, string id);

    /// <summary>Gate-Green: every gate command exited 0, at factory/&lt;id&gt;'s current head.</summary>
    bool GateGreen(Job job);

    /// <summary>Get-GitHubMerge: the job's PR as GitHub returns it when the human merged it there, else null.</summary>
    JsonObject? GitHubMerge(Job job);

    /// <summary>Complete-GitHubMerge: notes a head the gate never passed, then <see cref="CompleteMerge"/>.</summary>
    IReadOnlyList<string> CompleteGitHubMerge(IBoardSession session, Job job, JsonObject pr);

    /// <summary>Complete-Merge: what follows the PR's merge as <paramref name="sha"/>. Returns the lines it prints.</summary>
    IReadOnlyList<string> CompleteMerge(IBoardSession session, Job job, string sha);

    /// <summary>Remove-JobWork: closes the job's open PR, deletes factory/&lt;id&gt; on GitHub, and deletes its work dir.</summary>
    void RemoveJobWork(IBoardSession session, Job job);

    /// <summary>A preview still up is destroyed by a detached <c>factory destroy</c>.</summary>
    void StartDestroy(Job job);
}
