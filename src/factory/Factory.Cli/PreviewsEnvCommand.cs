using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>factory previews-env [--from &lt;file&gt;]</c> (contract, "Deploy"): the preview setup's <c>dotenv</c> output into .env,
/// by init's rule. Never HCP_ORG: turning deploys on stays the human's.
/// </summary>
public sealed class PreviewsEnvCommand(CliEnvironment host, IFactoryPaths paths, IProcessRunner processes, IFileStore files, EnvSettings env) : IStandaloneCommand
{
    public string Name => "previews-env";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var values = Outputs(args);
            var envPath = paths.EnvFile ?? throw new FactoryException("previews-env refused: no .env path; run it inside the factory's checkout");
            var current = Store.EnvFile.Parse(files.ReadText(envPath) ?? "");
            foreach (var name in PreviewsEnv.Settings)
            {
                if (!values.TryGetValue(name, out var value)) continue;
                var same = current.GetValueOrDefault(name) == value;
                if (!same) env.Set(envPath, name, value);
                stdout.WriteLine($"{name}: {(same ? "unchanged" : "set")}");
            }
            return 0;
        }
        catch (FactoryException e)
        {
            stderr.WriteLine($"factory: {e.Message}");
            return e.ExitCode;
        }
    }

    private Dictionary<string, string> Outputs(IReadOnlyList<string> args)
    {
        string json;
        if (args.Count == 0) json = Terraform();
        else if (args is ["--from", var from])
        {
            var path = Path.GetFullPath(from, host.WorkingDirectory);
            json = files.ReadText(path) ?? throw Refused($"{path} does not exist");
        }
        else throw new FactoryException("previews-env refused: usage: factory previews-env [--from <file>]", 2);
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw Refused("terraform output is not JSON"); }
        if (root?["dotenv"]?["value"] is not JsonObject map) throw Refused("terraform output has no dotenv map");
        return map.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.ToString(), StringComparer.Ordinal);
    }

    private string Terraform()
    {
        var dir = Path.GetFullPath(Path.Combine(paths.ScriptsDir, "..", "infra", "previews"));
        var r = processes.Run("terraform", [$"-chdir={dir}", "output", "-json"], dir);
        if (r.ExitCode == 0) return r.StdOut;
        // Terraform frames a diagnostic in box-drawing characters (╷ │ ╵); the first line with text is its summary.
        var why = r.StdErr.Split('\n').Select(l => l.Trim().Trim('╷', '│', '╵').Trim()).FirstOrDefault(l => l.Length > 0) ?? $"exit code {r.ExitCode}";
        throw Refused($"terraform output failed: {why}");
    }

    private static FactoryException Refused(string why) => new($"previews-env refused: {why}; run terraform apply in {PreviewsEnv.InfraDir} first");
}
