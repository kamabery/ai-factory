namespace Factory.Store.Abstractions;

/// <summary>
/// hcp.ps1: HCP Terraform over its API, with HCP_TOKEN (a team token) from .env as the bearer header only. Every call
/// returns or throws a one-line <see cref="Core.FactoryException"/> (<c>HCP &lt;status&gt; on &lt;method&gt; &lt;path&gt;: &lt;why&gt;</c>).
/// Waiting (a destroy run, a safe-delete HCP answers 409) is the caller's: each call here is one request.
/// </summary>
public interface IHcpClient
{
    /// <summary>The Azure variable set's variables (HCP_AZURE_VARSET); none when no varset is set.</summary>
    IReadOnlyList<HcpVar> VarsetVars();

    /// <summary>Ensure-HcpProject: the project's id, created if missing, with HCP_AZURE_VARSET attached to it.</summary>
    string EnsureProject(string name);

    /// <summary>
    /// Ensure-HcpWorkspace: the workspace's id, created in the project if missing. <paramref name="autoDestroyAt"/> is
    /// set on create and on every call, so a redeploy pushes the 24-hour timer out again.
    /// </summary>
    string EnsureWorkspace(string name, string projectId, string? autoDestroyAt = null);

    /// <summary>Get-HcpWorkspaceId: the workspace's id in HCP_ORG, or null when there is none.</summary>
    string? WorkspaceId(string name);

    /// <summary>Get-HcpOutputs: name to value of the workspace's outputs; a sensitive one is fetched only when named.</summary>
    IReadOnlyDictionary<string, string?> Outputs(string workspaceId, IReadOnlyCollection<string> sensitive);

    /// <summary>Get-HcpRunLog: the plan and apply logs of the workspace's latest run, as text.</summary>
    string RunLog(string workspaceId);

    /// <summary>Queues an auto-applied destroy run on the workspace.</summary>
    HcpRun QueueDestroy(string workspaceId);

    /// <summary>The run as HCP has it now.</summary>
    HcpRun Run(string runId);

    /// <summary>Safe-deletes the workspace. HCP answers 409 while it processes the state a destroy just wrote (J-025).</summary>
    void SafeDelete(string workspaceId);

    /// <summary>GET /account/details: the HCP_TOKEN's user name (doctor's check).</summary>
    string Account();
}
