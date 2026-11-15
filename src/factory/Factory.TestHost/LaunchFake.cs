using System.Diagnostics;
using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.TestHost;

/// <summary>
/// <c>launch --base &lt;dir&gt; -- &lt;command&gt;...</c>: a parent that starts <c>dotnet &lt;dir&gt;/factory.dll &lt;command&gt;</c>
/// detached, as the runner and the server do (FactoryLauncher), prints how many milliseconds the start took, and exits, so a
/// test sees that the child holds none of the parent's stdio.
/// </summary>
public static class LaunchFake
{
    public static int Run(IReadOnlyList<string> args)
    {
        var a = FakeArgs.Parse(args);
        var o = Options.Create(new FactoryOptions { BaseDirectory = a.Get("base") ?? AppContext.BaseDirectory });
        var paths = new FactoryPaths(o, new GitClient(new ProcessRunner()));
        var w = Stopwatch.StartNew();
        new FactoryLauncher(paths).StartDetached([.. a.Rest]);
        Console.WriteLine(w.ElapsedMilliseconds);
        return 0;
    }
}
