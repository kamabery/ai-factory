using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// canary.ps1's Canary-State and Canary-Fingerprint. The fingerprint must equal factory.ps1's byte for byte, or a
/// canary that passed under one implementation would not count under the other.
/// </summary>
public sealed partial class CanaryCheck(IFactoryPaths paths, IFileStore files, IDotEnv dotEnv, IHcpClient hcp) : ICanary
{
    // Sort-Object compares strings in the current culture, ignoring case.
    private static readonly StringComparer PsOrder = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);

    public CanaryState State()
    {
        var text = files.ReadText(Path.Combine(paths.Home, "canary.json"));
        return Canary.State(text is null ? null : JsonNode.Parse(text) as JsonObject, Fingerprint);
    }

    public string Fingerprint()
    {
        var lines = new List<string>();
        foreach (var f in new[] { "deploy.ps1", "hcp.ps1", "github.ps1" })
            lines.Add($"factory/{f} {Hash(Path.Combine(paths.ScriptsDir, f))}");
        var t = paths.TemplatesRoot;
        var inputs = files.Files(Path.Combine(t, "IaC")).Where(f => !DotTerraform().IsMatch(f.Path)).Select(f => f.Path).ToList();
        if (files.Stat(Path.Combine(t, "base-project", ".github", "workflows", "deploy.yml")) is { } deployYml) inputs.Add(deployYml.Path);
        var nupkg = Path.Combine(t, "artifacts", "nupkg");
        inputs.AddRange(files.Files(nupkg).Where(f => Path.GetDirectoryName(f.Path) == Path.GetFullPath(nupkg) && f.Path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path));
        foreach (var p in inputs.OrderBy(p => p, PsOrder))
            lines.Add($"{Path.GetRelativePath(t, p).Replace('\\', '/')} {Hash(p)}");
        foreach (var v in hcp.VarsetVars().OrderBy(v => v.Category, PsOrder).ThenBy(v => v.Key, PsOrder))
            lines.Add($"var {v.Category} {v.Key} {(v.Sensitive ? "<sensitive>" : v.Value ?? "")}");
        lines.Add($"canary {Repo()} {PsJson.WriteCompact(Options())}");
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
    }

    private string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(files.ReadBytes(path) ?? throw new FactoryException($"Cannot find path '{path}' because it does not exist.")));

    public string Repo() =>
        dotEnv.Get("FACTORY_CANARY_REPO")
        ?? (dotEnv.Get("GITHUB_OWNER") is { } owner ? $"{owner}/factory-canary" : throw new FactoryException("canary refused: set FACTORY_CANARY_REPO (or GITHUB_OWNER) in .env"));

    public JsonObject Options()
    {
        var sc = dotEnv.Get("FACTORY_CANARY_SCAFFOLD") is { } json
            ? JsonNode.Parse(json) as JsonObject
            : new JsonObject { ["name"] = "canary", ["resource"] = "Item", ["db"] = "postgres", ["ui"] = "react", ["events"] = false, ["auth"] = true };
        if (Scaffold.Error(sc) is { } err) throw new FactoryException($"canary refused: FACTORY_CANARY_SCAFFOLD: {err}");
        if (sc!.ContainsKey("auth") && PsJsonReader.Truthy(sc["auth"]))
        {
            var ids = new JsonObject
            {
                ["entra_external_tenant_id"] = dotEnv.Get("FACTORY_ENTRA_TENANT_ID"),
                ["entra_external_subdomain"] = dotEnv.Get("FACTORY_ENTRA_SUBDOMAIN"),
                ["entra_external_user_flow_id"] = dotEnv.Get("FACTORY_ENTRA_USER_FLOW_ID"),
            };
            if (ids.Any(kv => kv.Value is null))
                throw new FactoryException("canary refused: the canary scaffold has auth, and .env lacks FACTORY_ENTRA_TENANT_ID, FACTORY_ENTRA_SUBDOMAIN or FACTORY_ENTRA_USER_FLOW_ID (setup-azure.ps1 prints them)");
            sc["entra"] = ids;
        }
        return sc;
    }

    [GeneratedRegex(@"[\\/]\.terraform[\\/]", RegexOptions.IgnoreCase)] private static partial Regex DotTerraform();
}
