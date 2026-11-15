using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>factory/github-app.json, the GitHub App's manifest (contract, "Secrets, init and doctor").</summary>
public sealed class AppManifest(IFactoryPaths paths, IFileStore files)
{
    private string Path => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(paths.ScriptsDir))!, "github-app.json");

    /// <summary>The manifest as the repo holds it.</summary>
    public JsonObject Read() =>
        JsonNode.Parse(files.ReadText(Path) ?? throw new FactoryException($"{Path} is missing")) as JsonObject
            ?? throw new FactoryException($"{Path} is not a JSON object");

    /// <summary>The permissions it asks for: permission name to read or write.</summary>
    public IReadOnlyDictionary<string, string> Permissions() =>
        (Read()["default_permissions"] as JsonObject ?? []).ToDictionary(p => p.Key, p => PsJsonReader.Text(p.Value), StringComparer.Ordinal);

    /// <summary>
    /// The manifest init posts: the repo's, named for its owner (an App name is unique on GitHub), redirecting to this
    /// run's callback. The sign-in callback is registered without a port: GitHub lets a loopback redirect use any port, and
    /// a later init listens on another.
    /// </summary>
    public JsonObject For(string owner, string callbackBase)
    {
        var m = Read();
        m["name"] = $"{PsJsonReader.Text(m["name"])} ({owner})";
        m["url"] = $"https://github.com/{owner}";
        m["redirect_url"] = $"{callbackBase}/callback";
        m["callback_urls"] = new JsonArray("http://127.0.0.1/oauth");
        return m;
    }

    /// <summary>The permissions an installation lacks against the manifest: missing, or read where write is asked.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyDictionary<string, string> wanted, IReadOnlyDictionary<string, string> granted) =>
        [.. wanted.Where(w => !granted.TryGetValue(w.Key, out var g) || (w.Value == "write" && g != "write")).Select(w => $"{w.Key}: {w.Value}")];
}
