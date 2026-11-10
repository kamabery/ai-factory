using Factory.Core.Abstractions;

namespace Factory.Store;

public sealed class GitClient(IProcessRunner processes) : IGitClient
{
    public string? CommonDir(string workingDirectory)
    {
        var r = processes.Run("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"], workingDirectory);
        var dir = r.StdOut.Trim();
        return r.ExitCode == 0 && dir.Length > 0 ? dir : null;
    }

    public GitResult Run(string directory, params string[] args)
    {
        var r = processes.Run("git", ["-C", directory, .. args], "");
        return new GitResult(r.ExitCode, (r.StdOut + r.StdErr).Trim());
    }
}
