namespace Factory.Store;

/// <summary>A program found on PATH, by its full name.</summary>
public static class ToolPath
{
    /// <summary>
    /// The first <paramref name="path"/> folder's <paramref name="name"/>, as a full path; the bare name when none has
    /// it. A batch-file launcher (az.cmd) must be started this way: it finds its own folder through %~dp0, which is
    /// wrong when a process start resolved the bare name.
    /// </summary>
    public static string Find(string name, string? path)
    {
        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(full)) return full;
        }
        return name;
    }
}
