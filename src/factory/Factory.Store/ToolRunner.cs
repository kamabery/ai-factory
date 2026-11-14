using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// gh, az, node and the canary's scaffold commands as child processes, resolved from PATH (az is <c>az.cmd</c> on Windows).
/// An override script (tests) runs with <c>pwsh -NoProfile -File</c> and the same arguments.
/// </summary>
public sealed class ToolRunner(IProcessRunner processes, IFactoryPaths paths, IOptions<DeployOptions> options) : IToolRunner
{
    private DeployOptions O => options.Value;

    public ToolResult Gh(IReadOnlyList<string> args, string? stdin = null, IReadOnlyDictionary<string, string?>? environment = null) =>
        Tool(O.GhCmd, "gh", args, stdin, environment);

    public ToolResult Az(IReadOnlyList<string> args) => Tool(O.AzCmd, StateToken.AzProgram, args, null, null);

    public ToolResult Signin(IReadOnlyList<string> args, string password) =>
        string.IsNullOrEmpty(O.SigninCmd)
            ? Result(processes.Run("node", [Path.Combine(paths.ScriptsDir, "signin-smoke.mjs"), .. args], "", password, null))
            : Result(processes.Run("pwsh", ["-NoProfile", "-File", O.SigninCmd, .. args], "", password, null));

    public ToolResult Run(string directory, string program, IReadOnlyList<string> args) =>
        Result(processes.Run(program, args, directory, null, null));

    public ToolResult Pwsh(string directory, IReadOnlyList<string> args) =>
        Result(processes.Run("pwsh", ["-NoProfile", .. args], directory, null, null));

    private ToolResult Tool(string? overrideScript, string program, IReadOnlyList<string> args, string? stdin, IReadOnlyDictionary<string, string?>? env) =>
        Result(string.IsNullOrEmpty(overrideScript)
            ? processes.Run(program, args, "", stdin, env)
            : processes.Run("pwsh", ["-NoProfile", "-File", overrideScript, .. args], "", stdin, env));

    private static ToolResult Result(ProcessResult r)
    {
        var parts = new[] { r.StdOut.TrimEnd(), r.StdErr.TrimEnd() }.Where(p => p.Length > 0);
        return new ToolResult(r.ExitCode, string.Join("\n", parts).Trim());
    }
}
