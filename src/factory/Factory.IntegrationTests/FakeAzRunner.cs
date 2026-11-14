using Factory.Core.Abstractions;

namespace Factory.IntegrationTests;

/// <summary>An az that prints one access token and records what it was asked, for the Foundry probe's test.</summary>
public sealed class FakeAzRunner(string token) : IProcessRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        Calls.Add(arguments);
        return new ProcessResult(0, token + "\n", "");
    }

    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string? stdin, IReadOnlyDictionary<string, string?>? environment) =>
        Run(fileName, arguments, workingDirectory);

    public int RunAttached(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?>? environment) => 0;
}
