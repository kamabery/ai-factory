using Factory.Cli;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>What every command test needs: a home on an in-memory disk, paths over it, .env values and a fixed clock.</summary>
public abstract class CommandTestBase
{
    protected static readonly string Home = Path.Combine(Path.GetTempPath(), "cmd-home");
    protected static readonly string Work = Path.Combine(Path.GetTempPath(), "cmd-work");
    protected readonly MemoryFiles Files = new();
    protected readonly IFactoryPaths Paths = Substitute.For<IFactoryPaths>();
    protected readonly IDotEnv DotEnv = Substitute.For<IDotEnv>();
    protected readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
    protected const string Now = "2026-11-10T12:00:00.000Z";

    protected CommandTestBase()
    {
        // IDotEnv answers null for an unset value; a substitute would answer "".
        DotEnv.Get(Arg.Any<string>()).Returns((string?)null);
        Paths.Home.Returns(Home);
        Paths.EventsPath.Returns(Path.Combine(Home, "events.jsonl"));
        Paths.JobDir(Arg.Any<string>()).Returns(c => Path.Combine(Home, "jobs", c.Arg<string>()));
        Paths.WorkDir(Arg.Any<string>()).Returns(c => Path.Combine(Work, "jobs", c.Arg<string>()));
    }

    protected string JobFile(string id, string rel) => Path.Combine(Home, "jobs", id, rel.Replace('/', Path.DirectorySeparatorChar));

    protected static CommandArgs Args(params string[] args) => CommandArgs.Parse(args);
}
