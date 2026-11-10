using System.Text;
using Factory.Core.Abstractions;

namespace Factory.UnitTests;

/// <summary>An in-memory <see cref="IFileStore"/>: every write bumps the file's last-write time by one tick.</summary>
public sealed class MemoryFiles : IFileStore
{
    private readonly Dictionary<string, (byte[] Bytes, long Ticks)> _files = new(StringComparer.OrdinalIgnoreCase);
    private long _clock = 638_000_000_000_000_000;

    public List<string> Writes { get; } = [];

    public List<string> Directories { get; } = [];

    public void Put(string path, string text) => Set(path, Encoding.UTF8.GetBytes(text));

    public string? Text(string path) => _files.TryGetValue(path, out var f) ? Encoding.UTF8.GetString(f.Bytes) : null;

    public void Remove(string path) => _files.Remove(path);

    public string? ReadText(string path) => Text(path);

    public byte[]? ReadBytes(string path) => _files.TryGetValue(path, out var f) ? f.Bytes : null;

    public void EnsureDirectory(string path) => Directories.Add(path);

    public void WriteAtomic(string path, byte[] bytes) { Set(path, bytes); Writes.Add(path); }

    public void AppendText(string path, string text) =>
        Set(path, [.. ReadBytes(path) ?? [], .. Encoding.UTF8.GetBytes(text)]);

    public LocalFile? Stat(string path) => _files.TryGetValue(path, out var f) ? new LocalFile(path, f.Bytes.Length, f.Ticks) : null;

    public IReadOnlyList<LocalFile> Files(string root) =>
        [.. _files.Where(kv => kv.Key.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new LocalFile(kv.Key, kv.Value.Bytes.Length, kv.Value.Ticks))];

    public bool DirectoryExists(string path) =>
        Directories.Contains(path, StringComparer.OrdinalIgnoreCase) || _files.Keys.Any(k => k.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>Paths <see cref="DeleteDirectory"/> refuses, as a locked file would.</summary>
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void DeleteDirectory(string path)
    {
        if (Locked.Contains(path)) throw new IOException($"The process cannot access the file because it is being used by another process: '{path}'");
        foreach (var k in _files.Keys.Where(k => k.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList()) _files.Remove(k);
        Directories.RemoveAll(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase));
    }

    public void CopyFile(string from, string to) => Set(to, ReadBytes(from) ?? throw new FileNotFoundException(from));

    private void Set(string path, byte[] bytes) => _files[path] = (bytes, ++_clock);
}
