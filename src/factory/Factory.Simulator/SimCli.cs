using System.Diagnostics;
using System.Text;

namespace Factory.Simulator;

/// <summary>
/// The factory CLI as the simulator calls it, <c>dotnet &lt;factory.dll&gt; ...</c>, and git, each a child with the
/// simulator's own environment (<see cref="Env"/>) laid over this process's.
/// </summary>
public sealed class SimCli(string cliDll)
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public string Dll => cliDll;

    /// <summary>Variables set (or, null, removed) for every child.</summary>
    public Dictionary<string, string?> Env { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A command whose failure the simulation does not care about: its stdout, whatever its exit code.</summary>
    public string F(params string[] args) => Run("dotnet", [cliDll, .. args], null).Out;

    /// <summary>A command that must succeed: its stdout, trimmed.</summary>
    public string Must(params string[] args)
    {
        var r = Run("dotnet", [cliDll, .. args], null);
        if (r.Code != 0) throw new InvalidOperationException($"factory {string.Join(' ', args)} failed ({r.Code}): {r.Err.Trim()}");
        return r.Out.Trim();
    }

    public void Git(string? dir, params string[] args) => Run("git", dir is null ? args : ["-C", dir, .. args], null);

    private (int Code, string Out, string Err) Run(string program, IReadOnlyList<string> args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8, WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in Env)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, err.GetAwaiter().GetResult());
    }
}
