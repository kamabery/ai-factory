namespace Factory.Core.Abstractions;

/// <summary>The file system, as far as the factory's state needs it.</summary>
public interface IFileStore
{
    /// <summary>The file's text, or null when it does not exist.</summary>
    string? ReadText(string path);

    /// <summary>The file's bytes, read without locking out writers or a rename; null when it does not exist.</summary>
    byte[]? ReadBytes(string path);

    void EnsureDirectory(string path);

    /// <summary>
    /// Writes a temp file beside <paramref name="path"/>, then renames it over the target. The rename is retried
    /// for up to 2 s while a reader that did not share delete (serve.ps1's /state/board.json) holds the file.
    /// </summary>
    void WriteAtomic(string path, byte[] bytes);

    /// <summary>Appends UTF-8 text without a byte-order mark, creating the file if need be.</summary>
    void AppendText(string path, string text);

    /// <summary>Length and last-write time, or null when the file does not exist.</summary>
    LocalFile? Stat(string path);

    /// <summary>Every file under <paramref name="root"/>, hidden ones too; empty when the folder does not exist.</summary>
    IReadOnlyList<LocalFile> Files(string root);

    bool DirectoryExists(string path);

    /// <summary>Deletes a folder and everything in it, read-only files too (git's objects); a missing one is fine. Throws on a locked file.</summary>
    void DeleteDirectory(string path);

    /// <summary>Copies a file over <paramref name="to"/>, creating its folder.</summary>
    void CopyFile(string from, string to);

    /// <summary>
    /// Opens (creating) <paramref name="path"/> shared with nobody: the lock is held until the result is disposed.
    /// Null while another process holds it.
    /// </summary>
    IDisposable? TryLock(string path);
}
