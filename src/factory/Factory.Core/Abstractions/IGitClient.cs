namespace Factory.Core.Abstractions;

public interface IGitClient
{
    /// <summary>The absolute git common dir of the repository at <paramref name="workingDirectory"/>, or null outside one.</summary>
    string? CommonDir(string workingDirectory);

    /// <summary>
    /// factory.ps1's Invoke-Git: <c>git -C <paramref name="directory"/> args</c>, its exit code, and stdout and stderr
    /// together, trimmed.
    /// </summary>
    GitResult Run(string directory, params string[] args);
}
