namespace Factory.Store;

/// <summary>Where the claude CLI is: jobs (the runner) and idea turns (the CLI) start the same one.</summary>
public static class ClaudeCli
{
    /// <summary>claude from PATH: claude.exe, or the npm shim claude.cmd, on Windows; claude elsewhere.</summary>
    public static string Executable()
    {
        if (!OperatingSystem.IsWindows()) return "claude";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                var full = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(full)) return full;
            }
        return "claude";
    }
}
