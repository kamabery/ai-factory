using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>factory container serve|job</c> (contract, "Cloud mode (deployed)"): the image's entry point. az signs in as the
/// managed identity, the template workspace is cloned when missing, then the dashboard behind sign-in, or one job.
/// </summary>
public sealed class ContainerCommand(IFactoryPaths paths, IProcessRunner processes, IFileStore files, IPause pause, TemplateWorkspace templates, CliEnvironment host) : IStandaloneCommand
{
    private static readonly string[] Settings = ["GITHUB_", "HCP_", "AZURE_", "FACTORY_"];

    public string Name => "container";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var what = args.Count == 1 ? args[0] : null;
        if (what is not ("serve" or "job")) return Refuse(stderr, 2, "usage: factory container serve|job");
        if (host.Get("IDENTITY_ENDPOINT") is null) return Refuse(stderr, 1, "no managed identity (IDENTITY_ENDPOINT is unset)");

        string[] login = host.Get("AZURE_CLIENT_ID") is { } id
            ? ["login", "--identity", "--client-id", id, "--output", "none"]
            : ["login", "--identity", "--output", "none"];
        // The identity endpoint can answer empty while the container starts: three tries, 10 s apart.
        var r = processes.Run(StateToken.AzProgram, login, "");
        for (var attempt = 2; r.ExitCode != 0 && attempt <= 3; attempt++)
        {
            pause.Sleep(TimeSpan.FromSeconds(10));
            r = processes.Run(StateToken.AzProgram, login, "");
        }
        if (r.ExitCode != 0) return Refuse(stderr, 1, $"az login --identity failed: {(r.StdErr + r.StdOut).Trim()}");

        // The factory reads its settings from .env; a container has only its environment.
        if (paths.EnvFile is { } envFile)
        {
            var lines = host.Variables
                .Where(kv => kv.Value is { Length: > 0 } && Settings.Any(p => kv.Key.StartsWith(p, StringComparison.Ordinal))
                    && !SecretNames.All.Contains(kv.Key, StringComparer.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}\n");
            files.WriteAtomic(envFile, System.Text.Encoding.UTF8.GetBytes(string.Concat(lines)));
        }

        try { templates.Ensure(stdout); }
        catch (FactoryException e) { return Refuse(stderr, 1, e.Message); }

        var env = new Dictionary<string, string?> { ["FACTORY_CONTAINER"] = "1" };
        string[] command = what == "serve"
            ? [paths.ServerDll, "--port", host.Get("PORT") ?? "8080", "--require-signin"]
            : [paths.RunnerDll, "--one"];
        return processes.RunAttached("dotnet", command, host.WorkingDirectory, env);
    }

    private static int Refuse(TextWriter stderr, int code, string why)
    {
        stderr.WriteLine($"factory: container refused: {why}");
        return code;
    }
}
