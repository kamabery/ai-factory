using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// gh, az, node and the canary's scaffold commands as child processes, resolved from PATH (az is <c>az.cmd</c> on Windows).
/// An override (tests) is a program and its leading arguments, run with the real arguments after them (contract, "Implementations").
/// </summary>
public sealed class ToolRunner(IProcessRunner processes, IFactoryPaths paths, IOptions<DeployOptions> options) : IToolRunner
{
    private DeployOptions O => options.Value;

    public ToolResult Gh(IReadOnlyList<string> args, string? stdin = null, IReadOnlyDictionary<string, string?>? environment = null) =>
        Tool("FACTORY_GH_CMD", O.GhCmd, "gh", args, stdin, environment);

    public ToolResult Az(IReadOnlyList<string> args) => Tool("FACTORY_AZ_CMD", O.AzCmd, StateToken.AzProgram, args, null, null);

    public ToolResult Signin(IReadOnlyList<string> args, string password) =>
        string.IsNullOrEmpty(O.SigninCmd)
            ? Result(processes.Run("node", [Path.Combine(paths.ScriptsDir, "signin-smoke.mjs"), .. args], "", password, null))
            : Result(Override("FACTORY_SIGNIN_CMD", O.SigninCmd, args, "", password, null));

    public ToolResult Run(string directory, string program, IReadOnlyList<string> args) =>
        Result(processes.Run(program, args, directory, null, null));

    private ToolResult Tool(string name, string? fake, string program, IReadOnlyList<string> args, string? stdin, IReadOnlyDictionary<string, string?>? env) =>
        Result(string.IsNullOrEmpty(fake) ? processes.Run(program, args, "", stdin, env) : Override(name, fake, args, "", stdin, env));

    private ProcessResult Override(string name, string fake, IReadOnlyList<string> args, string directory, string? stdin, IReadOnlyDictionary<string, string?>? env)
    {
        var cmd = CommandOverride.Parse(name, fake);
        return processes.Run(cmd[0], [.. cmd.Skip(1), .. args], directory, stdin, env);
    }

    private static ToolResult Result(ProcessResult r)
    {
        var parts = new[] { r.StdOut.TrimEnd(), r.StdErr.TrimEnd() }.Where(p => p.Length > 0);
        return new ToolResult(r.ExitCode, string.Join("\n", parts).Trim());
    }
}
