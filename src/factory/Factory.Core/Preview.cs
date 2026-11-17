using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>deploy.ps1's naming and reading of a job's preview (contract, "Deploy").</summary>
public static partial class Preview
{
    /// <summary>A failed or half-applied attempt this many times over blocks the job.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Deploy-fix rounds a job may take before the next one blocks it.</summary>
    public const int MaxDeployFixes = 2;

    /// <summary>Preview-Env: <c>j</c> and the job id's digits (J-012 is j012; the canary is j000).</summary>
    public static string Env(string id) => "j" + NonDigits().Replace(id, "");

    /// <summary>
    /// Contract, "Nothing to preview": a clone with neither <c>IaC/</c> nor <c>.aspirestarter.json</c> has nothing the
    /// devops agent's author pass can deploy. False for a missing clone, so only a clone seen without them skips.
    /// </summary>
    public static bool NothingToDeploy(Abstractions.IFileStore files, string workDir) =>
        (files.DirectoryExists(Path.Combine(workDir, ".git")) || files.Stat(Path.Combine(workDir, ".git")) is not null)
        && !files.DirectoryExists(Path.Combine(workDir, "IaC"))
        && files.Stat(Path.Combine(workDir, ".aspirestarter.json")) is null;

    /// <summary>Repo-Name: the target's name, after the owner.</summary>
    public static string RepoName(string target) => target.Split('/', 2) is [_, var n] ? n : "";

    /// <summary>Get-Deploy: the job's deploy record, created empty (in the contract's key order) on first use.</summary>
    public static JsonObject Record(Job job)
    {
        if (job.Node["deploy"] is JsonObject d) return d;
        var env = Env(job.Id);
        d = new JsonObject
        {
            ["env"] = env, ["workspace"] = $"{RepoName(job.Target ?? "")}-{env}", ["workspaceId"] = null, ["status"] = null, ["attempts"] = 0,
            ["sha"] = null, ["run"] = null, ["urls"] = null, ["appliedAt"] = null, ["destroyAt"] = null,
        };
        job.Node["deploy"] = d;
        return d;
    }

    /// <summary>
    /// Preview-Urls: the front end (react_url, else blazor_web_url), the API, and /health as a browser reaches it. React's
    /// Static Web App links the API as its backend, which puts the API's own address behind the Static Web App's auth
    /// (it answers 401), so /health goes through the front end's /api.
    /// </summary>
    public static JsonObject Urls(IReadOnlyDictionary<string, string?> outputs)
    {
        var react = outputs.GetValueOrDefault("react_url");
        var api = outputs.GetValueOrDefault("api_url");
        var health = !string.IsNullOrEmpty(react) ? $"{react.TrimEnd('/')}/api/health"
            : !string.IsNullOrEmpty(api) ? $"{api.TrimEnd('/')}/health" : null;
        return new JsonObject { ["frontend"] = react ?? outputs.GetValueOrDefault("blazor_web_url"), ["api"] = api, ["health"] = health };
    }

    /// <summary>Whether the branch's deploy.yml declares a <c>since</c> input; GitHub refuses a dispatch with one it does not.</summary>
    public static bool DeclaresSince(string? deployYml) => deployYml is not null && Since().IsMatch(deployYml);

    /// <summary>
    /// The subject GitHub presents for the preview environment: the repo's <c>sub_claim_prefix</c> when it is on the
    /// immutable subject (the default for new repos, <c>repo:&lt;owner&gt;@&lt;id&gt;/&lt;name&gt;@&lt;id&gt;</c>; J-019 failed
    /// AADSTS700213 without it), else <c>repo:&lt;owner&gt;/&lt;name&gt;</c>.
    /// </summary>
    public static string OidcSubject(string target, JsonNode? oidc)
    {
        var prefix = oidc is JsonObject o && PsJsonReader.Truthy(o["use_immutable_subject"]) && PsJsonReader.Truthy(o["sub_claim_prefix"])
            ? PsJsonReader.Text(o["sub_claim_prefix"]) : $"repo:{target}";
        return $"{prefix}:environment:preview";
    }

    /// <summary>The federated credential's name on the GitHub app registration: <c>preview-&lt;owner&gt;-&lt;name&gt;</c>.</summary>
    public static string CredentialName(string target) => "preview-" + NotNameChar().Replace(target, "-");

    [GeneratedRegex(@"\D")] private static partial Regex NonDigits();
    [GeneratedRegex(@"(?m)^\s+since:", RegexOptions.IgnoreCase)] private static partial Regex Since();
    [GeneratedRegex("[^A-Za-z0-9-]", RegexOptions.IgnoreCase)] private static partial Regex NotNameChar();
}
