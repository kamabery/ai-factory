using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>factory idea [&lt;owner/name&gt; | I-NNN]</c> (contract, "Implementations"): the CLI door to an idea, an interactive
/// <c>claude --plugin-dir factory/plugin "/factory:idea ..."</c> session in this repo with this CLI first on PATH. An
/// existing target is read from a shallow clone in &lt;work&gt;/cli/&lt;owner&gt;-&lt;name&gt;, deleted when the session ends.
/// </summary>
public sealed partial class IdeaCommand(IFactoryPaths paths, IFileStore files, IProcessRunner processes, CliEnvironment host) : IStandaloneCommand
{
    public string Name => "idea";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var arg = args.Count > 0 ? args[0] : "";
        var repo = Path.GetFullPath(Path.Combine(paths.ScriptsDir, "..", ".."));
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = paths.CommandDir + Path.PathSeparator + host.Get("PATH"),
        };
        if (!OperatingSystem.IsWindows() && host.Get("DOTNET_ROOT") is null && host.DotnetRoot is { } root) env["DOTNET_ROOT"] = root;

        // The spec-writer reads the target; for a resumed idea, its hook events go to that idea.
        string? target = null;
        if (IdeaId().IsMatch(arg))
        {
            env["FACTORY_IDEA"] = arg;
            var status = Factory("status", "-Json");
            if (status.ExitCode == 0 && JsonNode.Parse(status.StdOut)?["ideas"] is JsonArray ideas)
                target = ideas.OfType<JsonObject>().FirstOrDefault(i => PsJsonReader.Is(PsJsonReader.Str(i["id"]), arg)) is { } idea ? PsJsonReader.Str(idea["target"]) : null;
        }
        else if (arg.Length > 0) target = arg;

        // --feature k (spec 22): feature k of the target's drafting repo idea, from its docs/PRODUCT.md on main.
        var suffix = "";
        var at = args.ToList().IndexOf("--feature");
        if (at >= 0 && target is not null)
        {
            var (why, from, k) = FeatureError(target, at + 1 < args.Count ? args[at + 1] : "");
            if (why is not null)
            {
                stderr.WriteLine($"factory: idea refused: {why}");
                return 1;
            }
            suffix = $" feature={k} from={from}";
        }

        string? clone = null;
        if (target is not null && Factory("check-target", target) is { ExitCode: 0 } check && check.StdOut.Trim() == "existing")
        {
            var dir = Path.Combine(paths.WorkRoot, "cli", target.Replace('/', '-'));
            var cloned = Factory("clone-target", target, dir);
            if (cloned.ExitCode == 0) clone = cloned.StdOut.Trim();
        }

        List<string> claude = ["--plugin-dir", Path.Combine(repo, "factory", "plugin")];
        if (clone is not null) claude.AddRange(["--add-dir", clone]);
        claude.Add($"/factory:idea {arg}{(clone is null ? "" : $" clone={clone}")}{suffix}".Trim());
        try { return processes.RunAttached(ClaudeCli.Executable(), claude, repo, env); }
        finally { if (clone is not null) try { files.DeleteDirectory(clone); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>Why feature <paramref name="k"/> of <paramref name="target"/> cannot start (null when it can), its repo idea and k.</summary>
    private (string? Why, string? From, int K) FeatureError(string target, string k)
    {
        if (!int.TryParse(k, System.Globalization.CultureInfo.InvariantCulture, out var n)) return ("--feature takes a number, the feature's place in docs/PRODUCT.md", null, 0);
        var status = Factory("status", "-Json");
        var repo = status.ExitCode == 0 && JsonNode.Parse(status.StdOut)?["ideas"] is JsonArray ideas
            ? ideas.OfType<JsonObject>().FirstOrDefault(i => PsJsonReader.Is(PsJsonReader.Str(i["target"]), target)
                && PsJsonReader.Is(PsJsonReader.Str(i["kind"]), "repo") && PsJsonReader.Is(PsJsonReader.Str(i["status"]), "drafting"))
            : null;
        if (repo is null) return ($"{target} has no drafting repo idea to start a feature of", null, n);
        var from = PsJsonReader.Str(repo["id"])!;
        var product = Factory("product", from);
        if (product.ExitCode != 0) return (product.StdErr.Trim(), from, n);
        var feature = (JsonNode.Parse(product.StdOut)?["features"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(f => PsJsonReader.Int(f["k"]) == n);
        if (feature is null) return ($"docs/PRODUCT.md on main has no feature {n}", from, n);
        if (PsJsonReader.Str(feature["idea"]) is { } started) return ($"feature {n} ({PsJsonReader.Str(feature["heading"])}) already has {started}", from, n);
        return (null, from, n);
    }

    private ProcessResult Factory(params string[] args) => processes.Run("dotnet", [paths.CliDll, .. args], "");

    [GeneratedRegex(@"^I-\d+$")] private static partial Regex IdeaId();
}
