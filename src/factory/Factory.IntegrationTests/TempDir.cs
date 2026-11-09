namespace Factory.IntegrationTests;

/// <summary>A fresh directory under the system temp folder, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "factory-it-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // git's read-only objects
            Directory.Delete(Path, true);
        }
        catch (IOException) { }
    }
}
