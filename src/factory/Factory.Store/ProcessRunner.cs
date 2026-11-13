using System.Diagnostics;
using Factory.Core.Abstractions;

namespace Factory.Store;

public sealed class ProcessRunner : IProcessRunner
{
    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory) =>
        Run(fileName, arguments, workingDirectory, null, null);

    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string? stdin,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Always piped, if only to be closed at once: a child that inherited this process's stdin would wait on it.
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        // Nobody is there to answer a prompt: a refused GitHub credential must fail, not wait (github.ps1 sets the same).
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (k, v) in environment ?? new Dictionary<string, string?>())
        {
            if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        }
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEndAsync();
        try { if (!string.IsNullOrEmpty(stdin)) p.StandardInput.Write(stdin); }
        catch (IOException) { }   // the child exited without reading it
        p.StandardInput.Close();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout.Result, stderr.Result);
    }
}
