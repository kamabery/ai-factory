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
}
