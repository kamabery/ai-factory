using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>Linux's /proc: a process whose command line names factory.dll, other than this one. Nothing elsewhere.</summary>
public sealed class DetachedCommands : IDetachedCommands
{
    public bool AnyRunning()
    {
        if (!OperatingSystem.IsLinux()) return false;
        var self = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var pid = Path.GetFileName(dir);
            if (pid == self || !pid.All(char.IsAsciiDigit)) continue;
            try
            {
                if (File.ReadAllText(Path.Combine(dir, "cmdline")).Contains("factory.dll", StringComparison.Ordinal)) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }
}
