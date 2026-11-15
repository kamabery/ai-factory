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

        string? clone = null;
        if (target is not null && Factory("check-target", target) is { ExitCode: 0 } check && check.StdOut.Trim() == "existing")
        {
            var dir = Path.Combine(paths.WorkRoot, "cli", target.Replace('/', '-'));
            var cloned = Factory("clone-target", target, dir);
            if (cloned.ExitCode == 0) clone = cloned.StdOut.Trim();
        }

        List<string> claude = ["--plugin-dir", Path.Combine(repo, "factory", "plugin")];
        if (clone is not null) claude.AddRange(["--add-dir", clone]);
        claude.Add($"/factory:idea {arg}{(clone is null ? "" : $" clone={clone}")}".Trim());
        try { return processes.RunAttached(ClaudeCli.Executable(), claude, repo, env); }
        finally { if (clone is not null) try { files.DeleteDirectory(clone); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private ProcessResult Factory(params string[] args) => processes.Run("dotnet", [paths.CliDll, .. args], "");

    [GeneratedRegex(@"^I-\d+$")] private static partial Regex IdeaId();
}
