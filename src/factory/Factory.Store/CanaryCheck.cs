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
/// The canary's state and fingerprint (contract, "Canary"): what the deploy path depends on, hashed, so a change to any
/// of it makes deployable jobs wait for a fresh canary pass.
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

    public string Fingerprint() => "sha256:" + Sha(string.Join("\n", Groups().SelectMany(g => g.Lines)));

    public IReadOnlyDictionary<string, string> Inputs() => Groups().ToDictionary(g => g.Name, g => Sha(string.Join("\n", g.Lines)));

    public string Version => PackageVersion;

    /// <summary>The fingerprint's lines in their four groups, in order (contract, "Canary", "Why it is due").</summary>
    private List<(string Name, List<string> Lines)> Groups()
    {
        var deploy = new List<string>();
        // A tool install has no sources: the package version stands for its deploy code (contract, "Canary").
        if (paths.IsToolInstall) deploy.Add($"factory {PackageVersion}");
        else
        {
            // factory/scripts -> factory -> the factory's own checkout, where the deploy code's sources are.
            var root = Path.GetFullPath(Path.Combine(paths.ScriptsDir, "..", ".."));
            foreach (var f in DeployCode)
                deploy.Add($"{f} {HashLf(Path.Combine(root, f))}");
        }
        var t = paths.TemplatesRoot;
        var inputs = files.Files(Path.Combine(t, "IaC")).Where(f => !DotTerraform().IsMatch(f.Path)).Select(f => f.Path).ToList();
        if (files.Stat(Path.Combine(t, "base-project", ".github", "workflows", "deploy.yml")) is { } deployYml) inputs.Add(deployYml.Path);
        var nupkg = Path.Combine(t, "artifacts", "nupkg");
        inputs.AddRange(files.Files(nupkg).Where(f => Path.GetDirectoryName(f.Path) == Path.GetFullPath(nupkg) && f.Path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path));
        var templates = inputs.OrderBy(p => p, PsOrder).Select(p => $"{Path.GetRelativePath(t, p).Replace('\\', '/')} {Hash(p)}").ToList();
        var varset = hcp.VarsetVars().OrderBy(v => v.Category, PsOrder).ThenBy(v => v.Key, PsOrder)
            .Select(v => $"var {v.Category} {v.Key} {(v.Sensitive ? "<sensitive>" : v.Value ?? "")}").ToList();
        List<string> settings = [$"canary {Repo()} {PsJson.WriteCompact(Options())}"];
        return [("deploy", deploy), ("templates", templates), ("varset", varset), ("settings", settings)];
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>The package version this build carries, without the source revision after '+'.</summary>
    public static string PackageVersion { get; } =
        (typeof(CanaryCheck).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0").Split('+')[0];

    /// <summary>The deploy path's .NET code (contract, "Canary"): a change to any of it invalidates the last canary.</summary>
    public static readonly IReadOnlyList<string> DeployCode =
    [
        "src/factory/Factory.Cli/DeployService.cs", "src/factory/Factory.Cli/CanaryService.cs",
        "src/factory/Factory.Store/HcpClient.cs", "src/factory/Factory.Store/GitHubClient.cs", "src/factory/Factory.Store/ToolRunner.cs",
    ];

    // Hashed with LF endings, so a Windows checkout (autocrlf) and a Linux one agree.
    private string HashLf(string path)
    {
        var text = Encoding.UTF8.GetString(files.ReadBytes(path) ?? throw new FactoryException($"Cannot find path '{path}' because it does not exist."));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));
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
                throw new FactoryException("canary refused: the canary scaffold has auth, and .env lacks FACTORY_ENTRA_TENANT_ID, FACTORY_ENTRA_SUBDOMAIN or FACTORY_ENTRA_USER_FLOW_ID (factory previews-env writes them)");
            sc["entra"] = ids;
        }
        return sc;
    }

    [GeneratedRegex(@"[\\/]\.terraform[\\/]", RegexOptions.IgnoreCase)] private static partial Regex DotTerraform();
}
