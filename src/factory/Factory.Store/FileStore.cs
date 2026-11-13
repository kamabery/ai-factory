using System.Text;
using Factory.Core.Abstractions;

namespace Factory.Store;

public sealed class FileStore : IFileStore
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public byte[]? ReadBytes(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(tmp, bytes);
        // factory.ps1's Replace-File: 40 retries 50 ms apart while a reader holds the target.
        for (var i = 0; ; i++)
        {
            try { File.Move(tmp, path, true); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < 40) { Thread.Sleep(50); }
        }
    }

    public void AppendText(string path, string text) => File.AppendAllText(path, text, Utf8);

    public LocalFile? Stat(string path)
    {
        var f = new FileInfo(path);
        return f.Exists ? new LocalFile(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks) : null;
    }

    public IReadOnlyList<LocalFile> Files(string root)
    {
        var d = new DirectoryInfo(root);
        if (!d.Exists) return [];
        var all = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        return [.. d.EnumerateFiles("*", all).Select(f => new LocalFile(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks))];
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void DeleteDirectory(string path)
    {
        var d = new DirectoryInfo(path);
        if (!d.Exists) return;
        foreach (var f in d.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            if (f.IsReadOnly) f.IsReadOnly = false;
        d.Delete(true);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void CopyFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
        File.Copy(from, to, true);
    }

    public IDisposable? TryLock(string path)
    {
        try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
