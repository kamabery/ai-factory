using System.Diagnostics;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// <c>dotnet &lt;factory.dll&gt; &lt;args&gt;</c>, detached, so it outlives this process (destroy, canary-destroy, sweep,
/// sync-prs, runner, idea-turn). It must inherit none of this process's stdio: a caller reading our output (the runner)
/// would otherwise wait on it until the child ends, or the child would die writing to a pipe nobody reads. On Windows it
/// is shell-executed in a hidden window; elsewhere <c>/bin/sh</c> starts it with all three on /dev/null.
/// </summary>
public sealed class FactoryLauncher(IFactoryPaths paths) : IFactoryLauncher
{
    public void StartDetached(IReadOnlyList<string> args)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("dotnet") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var a in (string[])[paths.CliDll, .. args]) psi.ArgumentList.Add(a);
        }
        else
        {
            // $0 is the DLL, "$@" the arguments: nothing is interpolated into the shell text.
            psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (var a in (string[])["-c", "exec dotnet \"$0\" \"$@\" </dev/null >/dev/null 2>&1", paths.CliDll, .. args])
                psi.ArgumentList.Add(a);
        }
        using var _ = Process.Start(psi);
    }
}
