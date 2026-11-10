namespace Factory.Core.Abstractions;

public interface IGitClient
{
    /// <summary>The absolute git common dir of the repository at <paramref name="workingDirectory"/>, or null outside one.</summary>
    string? CommonDir(string workingDirectory);
}
