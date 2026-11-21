using Factory.Server;
using Factory.Server.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>What the dashboard server's tests share: factory.ps1, the launcher and the process table as substitutes.</summary>
public abstract class ServerTestBase : CommandTestBase
{
    protected readonly IFactoryCalls Factory = Substitute.For<IFactoryCalls>();
    protected readonly IFactoryLauncher Launcher = Substitute.For<IFactoryLauncher>();
    protected readonly IProcessProbe Procs = Substitute.For<IProcessProbe>();
    protected readonly IStateCache Cache = Substitute.For<IStateCache>();
    protected readonly IServerSettings Settings = Substitute.For<IServerSettings>();
    protected readonly IStateLeases Leases = Substitute.For<IStateLeases>();
    protected readonly IStateSync Sync = Substitute.For<IStateSync>();
    protected readonly IStateSettings State = Substitute.For<IStateSettings>();
    protected IRunnerStarter Runner;
    protected readonly ServerOptions Options = new() { EnvFile = Path.Combine(Home, ".env"), WorkRoot = Work, WatchdogSeconds = 10, PrSyncSeconds = 30 };

    protected ServerTestBase()
    {
        Paths.BoardPath.Returns(Path.Combine(Home, "board.json"));
        Runner = new Factory.Store.RunnerStarter(Microsoft.Extensions.Options.Options.Create(new Factory.Store.CloudOptions()),
            Microsoft.Extensions.Options.Options.Create(new Factory.Store.StateOptions()), Launcher, Substitute.For<Factory.Core.Abstractions.IProcessRunner>());
        Procs.CurrentPid.Returns(4242);
        Factory.Run(Arg.Any<string[]>()).Returns(CallResult.Done(""));
        Settings.Env(Arg.Any<string>()).Returns((string?)null);
        Settings.Mode.Returns("local");
        Settings.IdeaBlocker.Returns((string?)null);
        Settings.IdeaModel.Returns("claude-opus-5-5");
    }

    protected IOptions<ServerOptions> Opts => Microsoft.Extensions.Options.Options.Create(Options);

    protected BoardFile Board => new(Paths, Files);

    protected JobsApi Jobs => new(Factory, Launcher, Runner, Paths, Files, Board, Cache, Procs);

    protected SessionFiles Sessions => new(Paths, Files, Procs, Opts, Clock);

    protected IdeaSessions Ideas => new(Factory, Launcher, Paths, Files, Settings, Board, Sessions, Jobs, Sync, State, Opts, Clock);

    protected ReadsApi Reads => new(Factory, Paths, Files, Settings, Procs, Board);

    protected Watchdog Dog => new(Board, Paths, Files, Launcher, Runner, Leases, Factory, Procs, Ideas, Opts, Clock, NullLogger<Watchdog>.Instance);

    /// <summary>A board.json holding these jobs (id, stage, and an optional pr).</summary>
    protected void SetBoard(params (string Id, string Stage, bool Pr)[] jobs) =>
        Files.Put(Path.Combine(Home, "board.json"),
            "{\"jobs\":[" + string.Join(',', jobs.Select(j => $"{{\"id\":\"{j.Id}\",\"stage\":\"{j.Stage}\",\"title\":\"t {j.Id}\"{(j.Pr ? ",\"pr\":{\"number\":1}" : "")}}}")) + "],\"ideas\":[]}");

    protected static string Body(Reply r) => r.Body;
}
