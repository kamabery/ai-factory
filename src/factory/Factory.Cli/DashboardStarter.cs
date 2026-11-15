using System.Globalization;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>init's last step: the dashboard already serving this state, or a new one on it (contract, "Secrets, init and doctor").</summary>
public sealed class DashboardStarter(IFactoryPaths paths, IFileStore files, IFactoryLauncher launcher)
{
    /// <summary>
    /// The dashboard's URL: serve.lock's when its PID is alive, else a new detached server's on <paramref name="freePort"/>.
    /// A new one is given this state dir and .env: without them it would fall back to the main checkout's .env and state.
    /// </summary>
    public string Start(Func<int, bool> alive, int freePort)
    {
        var held = files.ReadText(Path.Combine(paths.Home, "serve.lock"))?.Split('\n').Select(l => l.Trim()).ToArray();
        if (held is { Length: >= 2 } && int.TryParse(held[0], CultureInfo.InvariantCulture, out var pid) && alive(pid) && held[1].Length > 0) return held[1];
        List<string> args = ["serve", "--port", freePort.ToString(CultureInfo.InvariantCulture), "--state-dir", paths.Home];
        if (paths.EnvFile is { } env) args.AddRange(["--env-file", env]);
        launcher.StartDetached(args);
        return $"http://localhost:{freePort}/";
    }
}
