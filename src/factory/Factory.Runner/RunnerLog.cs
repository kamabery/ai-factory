using System.Globalization;
using System.Text;
using Factory.Core.Abstractions;
using Factory.Runner.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Runner;

/// <summary><c>&lt;FACTORY_HOME&gt;/runner.log</c>: one line per entry, its UTC time in round-trip form first.</summary>
public sealed class RunnerLog(IFactoryPaths paths, IFileStore files, TimeProvider clock) : IRunnerLog
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public void Write(string message) =>
        files.AppendText(Path.Combine(paths.Home, "runner.log"),
            clock.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
}
