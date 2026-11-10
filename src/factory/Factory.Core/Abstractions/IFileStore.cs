namespace Factory.Core.Abstractions;

/// <summary>The file system, as far as the factory's state needs it.</summary>
public interface IFileStore
{
    /// <summary>The file's text, or null when it does not exist.</summary>
    string? ReadText(string path);

    void EnsureDirectory(string path);
}
