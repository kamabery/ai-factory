using Microsoft.Extensions.DependencyInjection;

namespace Factory.Cli;

public static class CliHost
{
    /// <summary>Runs one command and writes its output; returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, string workingDirectory,
        TextWriter stdout, TextWriter stderr, TextReader? stdin = null, IReadOnlyList<string>? self = null)
    {
        var host = new CliEnvironment(env, workingDirectory, stdout.NewLine,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(), AppContext.BaseDirectory, self, Environment.ProcessId,
            // <root>/shared/Microsoft.NETCore.App/<version>/ is where this runtime lives.
            Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")));
        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(host));
        // A standalone command (idea-turn, idea-mcp, the hook's event) runs without opening the board.
        var standalone = args.Count > 0
            ? provider.GetServices<Abstractions.IStandaloneCommand>().FirstOrDefault(c => string.Equals(c.Name, args[0], StringComparison.OrdinalIgnoreCase) && c.Accepts([.. args.Skip(1)]))
            : null;
        if (standalone is not null) return standalone.Run([.. args.Skip(1)], stdin ?? TextReader.Null, stdout, stderr);
        var r = provider.GetRequiredService<CliApp>().Run(args);
        foreach (var line in r.Out) stdout.WriteLine(line);
        foreach (var line in r.Err) stderr.WriteLine(line);
        return r.ExitCode;
    }
}
