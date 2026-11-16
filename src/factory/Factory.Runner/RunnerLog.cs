using System.Globalization;
using System.Text;
using Factory.Core.Abstractions;
using Factory.Runner.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Runner;

/// <summary><c>&lt;FACTORY_HOME&gt;/runner.log</c>: one line per entry, its UTC time in round-trip form first.</summary>
public sealed class RunnerLog(IFactoryPaths paths, IFileStore files, TimeProvider clock, Microsoft.Extensions.Options.IOptions<RunnerOptions> options, TextWriter? stdout = null) : IRunnerLog
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>In a container (FACTORY_CONTAINER), each line goes to stdout too, so Log Analytics has the runs.</summary>
    public void Write(string message)
    {
        var line = clock.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + " " + message;
        files.AppendText(Path.Combine(paths.Home, "runner.log"), line + Environment.NewLine);
        if (options.Value.Echo) (stdout ?? Console.Out).WriteLine("runner " + line);
    }
}
