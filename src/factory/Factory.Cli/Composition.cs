using Factory.Core;
using Factory.Store;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.Cli;

/// <summary>The CLI's composition root: the only place that reads the environment.</summary>
public static class Composition
{
    public static IServiceCollection AddFactoryCli(this IServiceCollection services,
        IReadOnlyDictionary<string, string?> env, string workingDirectory, string newLine)
    {
        services.Configure<FactoryOptions>(o =>
        {
            o.Home = env.GetValueOrDefault("FACTORY_HOME");
            o.Repo = env.GetValueOrDefault("FACTORY_REPO");
            o.WorkingDirectory = workingDirectory;
        });
        services.Configure<CliOptions>(o => o.NewLine = newLine);

        services.AddSingleton<IFileStore, FileStore>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IFactoryPaths, FactoryPaths>();
        services.AddSingleton<IBoardReader, BoardReader>();
        services.AddSingleton<ICommand, StatusCommand>();
        services.AddSingleton<CliApp>();
        return services;
    }

    public static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}

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
