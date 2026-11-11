using System.Diagnostics;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// <c>pwsh -NoProfile -File factory/scripts/factory.ps1 &lt;args&gt;</c>, detached, so it outlives this process. Commands not yet
/// ported (destroy, spec 09) run there. It must inherit none of this process's stdio: a caller reading our output (the
/// runner) would otherwise wait on it until the child ends, or the child would die writing to a pipe nobody reads. On
/// Windows it is shell-executed in a hidden window; elsewhere <c>/bin/sh</c> starts it with all three on /dev/null.
/// </summary>
public sealed class FactoryLauncher(IFactoryPaths paths) : IFactoryLauncher
{
    public void StartDetached(IReadOnlyList<string> args)
    {
        var script = Path.Combine(paths.ScriptsDir, "factory.ps1");
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("pwsh") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var a in (string[])["-NoProfile", "-File", script, .. args]) psi.ArgumentList.Add(a);
        }
        else
        {
            // $0 is the script, "$@" the arguments: nothing is interpolated into the shell text.
            psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (var a in (string[])["-c", "exec pwsh -NoProfile -File \"$0\" \"$@\" </dev/null >/dev/null 2>&1", script, .. args])
                psi.ArgumentList.Add(a);
        }
        using var _ = Process.Start(psi);
    }
}
