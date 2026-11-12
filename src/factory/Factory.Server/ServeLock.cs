using System.Globalization;
using Factory.Server.Abstractions;

namespace Factory.Server;

/// <summary>
/// serve.lock: one dashboard per state dir, so one watchdog and one PR sync. Its PID, then its URL once bound; a lock
/// whose PID is gone is taken over, as runner.lock's is.
/// </summary>
public sealed class ServeLock(string home, IProcessProbe procs)
{
    public string Path { get; } = System.IO.Path.Combine(home, "serve.lock");

    /// <summary>Takes the lock, or returns the live holder's PID and URL.</summary>
    public (bool Taken, string? HolderPid, string? HolderUrl) TryTake()
    {
        if (CreateNew()) return (true, null, null);
        var held = Read();
        if (held.Length > 0 && int.TryParse(held[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && procs.IsAlive(pid))
            return (false, held[0], held.Length > 1 ? held[1] : null);
        try { File.Delete(Path); } catch (IOException) { }
        return (CreateNew(), null, null);
    }

    /// <summary>Set-Content: the PID, then the URL once it is known, each line ended.</summary>
    public void Write(string? url) =>
        File.WriteAllText(Path, procs.CurrentPid.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + (url is null ? "" : url + Environment.NewLine));

    /// <summary>Removes the lock if it is still this process's.</summary>
    public void Release()
    {
        var held = Read();
        if (held.Length > 0 && held[0] == procs.CurrentPid.ToString(CultureInfo.InvariantCulture))
            try { File.Delete(Path); } catch (IOException) { }
    }

    private bool CreateNew()
    {
        try { using var _ = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    private string[] Read()
    {
        try { return File.ReadAllLines(Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
