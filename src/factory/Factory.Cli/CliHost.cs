using Microsoft.Extensions.DependencyInjection;

namespace Factory.Cli;

public static class CliHost
{
    /// <summary>Runs one command and writes its output; returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, string workingDirectory,
        TextWriter stdout, TextWriter stderr)
    {
        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(env, workingDirectory, stdout.NewLine));
        var r = provider.GetRequiredService<CliApp>().Run(args);
        foreach (var line in r.Out) stdout.WriteLine(line);
        foreach (var line in r.Err) stderr.WriteLine(line);
        return r.ExitCode;
    }
}
