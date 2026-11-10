using System.Diagnostics;

namespace Factory.IntegrationTests;

/// <summary>A real git repository in a temp folder, for tests of what factory.ps1 asks git.</summary>
public sealed class GitRepo
{
    public GitRepo(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
        Git("init", "-q", "-b", "main");
    }

    public string Path { get; }

    /// <summary>Writes the files and commits them with <paramref name="message"/>; returns the commit's sha.</summary>
    public string Commit(string message, params (string Rel, string Text)[] files)
    {
        foreach (var (rel, text) in files)
        {
            var p = System.IO.Path.Combine(Path, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr.Result}");
        return stdout;
    }
}
