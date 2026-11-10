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
            UseShellExecute = false,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout, stderr.Result);
    }
}
