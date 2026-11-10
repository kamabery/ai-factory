using Factory.Core;

namespace Factory.Store;

public sealed class FileStore : IFileStore
{
    public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);
}
