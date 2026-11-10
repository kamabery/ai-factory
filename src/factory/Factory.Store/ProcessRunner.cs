using System.Diagnostics;
using Factory.Core.Abstractions;

namespace Factory.Store;

public sealed class ProcessRunner : IProcessRunner
{
    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Closed at once: a child that inherited this process's stdin would wait on it (state.ps1 pipes '' in).
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        // Nobody is there to answer a prompt: a refused GitHub credential must fail, not wait (github.ps1 sets the same).
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout, stderr.Result);
    }
}
