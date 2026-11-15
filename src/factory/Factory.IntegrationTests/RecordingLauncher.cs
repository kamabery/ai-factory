using Factory.Store.Abstractions;

namespace Factory.IntegrationTests;

/// <summary>A launcher that records the detached starts it was asked for and starts nothing.</summary>
public sealed class RecordingLauncher : IFactoryLauncher
{
    public List<IReadOnlyList<string>> Started { get; } = [];

    public void StartDetached(IReadOnlyList<string> args) => Started.Add(args);
}
