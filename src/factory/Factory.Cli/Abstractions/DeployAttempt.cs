using System.Text.Json.Nodes;

namespace Factory.Cli.Abstractions;

/// <summary>
/// One deploy attempt's outcome (deploy.ps1's Invoke-Deploy): whether the preview is up and smoke-tested, the head it
/// was pushed at, the GitHub run, the urls, HCP's destroy time and workspace, and on a failure the deploy-&lt;a&gt;.log text.
/// </summary>
public sealed record DeployAttempt(bool Ok, string? Sha, string? Run, JsonObject? Urls, string? DestroyAt, string? WorkspaceId, string Log);
