using Factory.Core.Abstractions;
using Factory.Server.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Server;

/// <summary>
/// <c>pwsh -NoProfile -File factory/scripts/factory.ps1 &lt;args&gt;</c>, as serve.ps1's Invoke-Factory ran it: ok is exit 0,
/// and the message is the lines of stdout and stderr together, trimmed, without a leading <c>factory: </c>.
/// </summary>
public sealed class FactoryCalls(IFactoryPaths paths, IProcessRunner processes) : IFactoryCalls
{
    public CallResult Run(params string[] args)
    {
        var r = processes.Run("pwsh", ["-NoProfile", "-File", Path.Combine(paths.ScriptsDir, "factory.ps1"), .. args], "");
        var message = string.Join('\n', Lines(r.StdOut).Concat(Lines(r.StdErr))).Trim();
        if (message.StartsWith("factory: ", StringComparison.Ordinal)) message = message["factory: ".Length..];
        return new CallResult(r.ExitCode == 0, message);
    }

    /// <summary>2>&amp;1 collects each stream's lines: a stream's final newline ends its last line and adds no empty one.</summary>
    private static IEnumerable<string> Lines(string text)
    {
        var t = text.Replace("\r\n", "\n");
        if (t.EndsWith('\n')) t = t[..^1];
        return t.Length == 0 ? [] : t.Split('\n').Select(l => l.TrimEnd());
    }
}
