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
public sealed class ContainerCommand(IFactoryPaths paths, IProcessRunner processes, TemplateWorkspace templates, CliEnvironment host) : IStandaloneCommand
{
    public string Name => "container";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var what = args.Count == 1 ? args[0] : null;
        if (what is not ("serve" or "job")) return Refuse(stderr, 2, "usage: factory container serve|job");
        if (host.Get("IDENTITY_ENDPOINT") is null) return Refuse(stderr, 1, "no managed identity (IDENTITY_ENDPOINT is unset)");

        string[] login = host.Get("AZURE_CLIENT_ID") is { } id
            ? ["login", "--identity", "--client-id", id, "--output", "none"]
            : ["login", "--identity", "--output", "none"];
        var r = processes.Run(StateToken.AzProgram, login, "");
        if (r.ExitCode != 0) return Refuse(stderr, 1, $"az login --identity failed: {(r.StdErr + r.StdOut).Trim()}");

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
