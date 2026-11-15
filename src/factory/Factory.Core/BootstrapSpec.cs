using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>factory.ps1's Bootstrap-Spec: factory/templates/bootstrap.md with the human's scaffold options filled in.</summary>
public static partial class BootstrapSpec
{
    /// <summary>The React app's install and build, each a gate line run with no shell (contract, "Gate").</summary>
    private static readonly string[] UiGate = ["- ui-deps: `npm --prefix src/ui/react ci --no-audit --no-fund`", "- ui: `npm --prefix src/ui/react run build`"];

    /// <summary>The spec's lines, split as <c>-split "`r?`n"</c> splits them (a final newline leaves an empty last line).</summary>
    public static IReadOnlyList<string> Lines(JsonObject sc, string target, string template, string templatesRoot)
    {
        var n = PsJsonReader.Text(sc["name"]);
        List<string> gate = [$"- build: `dotnet build {n}.slnx`", $"- test: `dotnet test {n}.slnx`", "- apphost: `dotnet build .aspire/apphost.cs`", "- infra: `aspire-addon infra check`"];
        if (PsJsonReader.Is(PsJsonReader.Str(sc["ui"]), "react")) gate.AddRange(UiGate);
        var values = new Dictionary<string, string>
        {
            ["name"] = n,
            ["resource"] = PsJsonReader.Text(sc["resource"]),
            ["db"] = PsJsonReader.Text(sc["db"]),
            ["ui"] = PsJsonReader.Text(sc["ui"]),
            ["events"] = Bool(sc["events"]),
            ["auth"] = Bool(sc["auth"]),
            ["target"] = target,
            ["templates"] = templatesRoot,
            ["gate"] = string.Join("\n", gate),
            ["commands"] = string.Join("\n", Scaffold.Commands(sc).Select(c => $"  - `{c}`")),
        };
        var text = template;
        foreach (var (k, v) in values) text = text.Replace("{{" + k + "}}", v, StringComparison.Ordinal);
        return NewLine().Split(text);
    }

    private static string Bool(JsonNode? n) => PsJsonReader.Truthy(n) ? "true" : "false";

    [GeneratedRegex("\r?\n")] private static partial Regex NewLine();
}
