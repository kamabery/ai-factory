using System.Text.Json;
using System.Text.Json.Nodes;

namespace Factory.IntegrationTests;

/// <summary>Factory.TestHost's fakes, as a test override names them: a program and its leading arguments (contract, "Implementations").</summary>
public static class Fakes
{
    /// <summary>The TestHost build beside this one's.</summary>
    public static string TestHostDll => GitHubStub.RepoFile("artifacts", "bin", "Factory.TestHost", "debug", "Factory.TestHost.dll");

    /// <summary>The override value for <c>dotnet Factory.TestHost.dll &lt;mode&gt; &lt;leading&gt;...</c>: a JSON array.</summary>
    public static string Command(string mode, params string[] leading) =>
        new JsonArray([.. new[] { "dotnet", TestHostDll, mode }.Concat(leading).Select(s => (JsonNode?)s)]).ToJsonString();

    /// <summary>The fake tool's log, one JSON object per call.</summary>
    public static IReadOnlyList<JsonObject> Log(string path) =>
        File.Exists(path) ? [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())] : [];

    public static string[] Args(JsonObject call) => call["args"]!.Deserialize<string[]>()!;
}
