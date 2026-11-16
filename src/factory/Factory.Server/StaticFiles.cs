using Factory.Server.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Server;

/// <summary>GETs of the dashboard (index.html and its bundle under /assets/...) and read-only files under the state dir (/state/...); nothing outside them.</summary>
public sealed class StaticFiles(IFactoryPaths paths, IStateCache cache, IOptions<ServerOptions> options)
{
    public Reply Index() => Reply.File(Path.Combine(options.Value.DashboardDir, "index.html"));

    /// <summary><paramref name="rest"/> is the path after <c>/state/</c>. In azure, the board and events are refreshed first.</summary>
    public Reply State(string rest)
    {
        var root = Path.GetFullPath(paths.Home);
        var full = Path.GetFullPath(Path.Combine(root, rest));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Reply.BadPath();
        if (rest == "board.json") cache.Board();
        if (rest == "events.jsonl") cache.Events();
        return Reply.File(full);
    }

    /// <summary>The SPA's bundle: <paramref name="rest"/> is the path after <c>/assets/</c>.</summary>
    public Reply Assets(string rest)
    {
        var root = Path.GetFullPath(Path.Combine(options.Value.DashboardDir, "assets")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, rest));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Reply.File(full) : Reply.BadPath();
    }
}
