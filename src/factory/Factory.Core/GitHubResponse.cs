using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>
/// github.ps1's Invoke-GitHub result. <paramref name="Status"/> 0 means no answer (no credential, or the request
/// itself failed). <paramref name="Permission"/> is GitHub's X-Accepted-GitHub-Permissions on a 403: what the
/// credential lacks.
/// </summary>
public sealed record GitHubResponse(int Status, JsonNode? Body, string? Permission);
