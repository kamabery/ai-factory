using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>factory init cloud [--from &lt;file&gt;]</c> (contract, "Cloud mode (deployed)"): after <c>terraform apply</c> in
/// factory/infra/cloud, each secret this machine's store holds goes into the deployed factory's vault (never printed),
/// and the dashboard's URL is printed. This machine's .env and board are left as they are.
/// </summary>
public sealed class InitCloud(CliEnvironment host, IFactoryPaths paths, IProcessRunner processes, IFileStore files, ISecretStore local, Func<string, ISecretStore> vaultFor)
{
    public const string InfraDir = "factory/infra/cloud";

    public int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var cloud = Outputs(args);
            if (cloud["vault_uri"] is not JsonValue uri) throw Refused("terraform output has no cloud vault_uri");
            var vault = vaultFor(uri.ToString());
            foreach (var name in SecretNames.All)
            {
                if (local.Get(name) is not { } value) continue;
                var same = vault.Get(name) == value;
                if (!same) vault.Set(name, value);
                stdout.WriteLine($"{name}: {(same ? "unchanged" : "set")}");
            }
            if (cloud["dashboard_url"] is JsonValue url) stdout.WriteLine($"dashboard: {url}");
            return 0;
        }
        catch (FactoryException e)
        {
            stderr.WriteLine($"factory: {e.Message}");
            return e.ExitCode;
        }
    }

    private JsonObject Outputs(IReadOnlyList<string> args)
    {
        string json;
        if (args.Count == 0) json = Terraform();
        else if (args is ["--from", var from])
        {
            var path = Path.GetFullPath(from, host.WorkingDirectory);
            json = files.ReadText(path) ?? throw Refused($"{path} does not exist");
        }
        else throw new FactoryException("init cloud refused: usage: factory init cloud [--from <file>]", 2);
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw Refused("terraform output is not JSON"); }
        return root?["cloud"]?["value"] as JsonObject ?? throw Refused("terraform output has no cloud vault_uri");
    }

    private string Terraform()
    {
        var dir = Path.GetFullPath(Path.Combine(paths.ScriptsDir, "..", "infra", "cloud"));
        ProcessResult r;
        try { r = processes.Run("terraform", [$"-chdir={dir}", "output", "-json"], dir); }
        catch (System.ComponentModel.Win32Exception e) { throw Refused($"terraform output failed: {e.Message}"); }
        if (r.ExitCode == 0) return r.StdOut;
        var why = r.StdErr.Split('\n').Select(l => l.Trim().Trim('╷', '│', '╵').Trim()).FirstOrDefault(l => l.Length > 0) ?? $"exit code {r.ExitCode}";
        throw Refused($"terraform output failed: {why}");
    }

    private static FactoryException Refused(string why) => new($"init cloud refused: {why}; run terraform apply in {InfraDir} first");
}
