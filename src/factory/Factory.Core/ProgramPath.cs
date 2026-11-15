namespace Factory.Core;

/// <summary>
/// Which file a gate line's program starts (contract, "Gate"). On Windows, CreateProcess adds only <c>.exe</c>, so the
/// program is looked up as cmd would: as given when it has a folder, else in each PATH folder, each time as named (when it
/// has an extension) and then with each PATHEXT extension. Elsewhere the OS finds it. Not found, it is returned as given.
/// </summary>
public static class ProgramPath
{
    public static string Resolve(string program, string path, string pathExt, bool windows, Func<string, bool> exists)
    {
        if (!windows) return program;
        var exts = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<string> Candidates(string full) =>
            (Path.HasExtension(full) ? [full] : Array.Empty<string>()).Concat(exts.Select(e => full + e.ToLowerInvariant()));
        var folders = Path.GetDirectoryName(program) is { Length: > 0 }
            ? [""]
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim('"'));
        foreach (var folder in folders)
            foreach (var c in Candidates(folder.Length == 0 ? program : Path.Combine(folder, program)))
                if (exists(c)) return c;
        return program;
    }
}
