using System.Diagnostics;
using Factory.Core;

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

public sealed class GitClient(IProcessRunner processes) : IGitClient
{
    public string? CommonDir(string workingDirectory)
    {
        var r = processes.Run("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"], workingDirectory);
        var dir = r.StdOut.Trim();
        return r.ExitCode == 0 && dir.Length > 0 ? dir : null;
    }
}
