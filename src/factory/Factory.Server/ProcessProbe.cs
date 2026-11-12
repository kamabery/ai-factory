using System.Diagnostics;
using Factory.Server.Abstractions;

namespace Factory.Server;

public sealed class ProcessProbe : IProcessProbe
{
    public int CurrentPid => Environment.ProcessId;

    public bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    public DateTime? StartTime(int pid)
    {
        if (pid <= 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.HasExited ? null : p.StartTime.ToUniversalTime(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
