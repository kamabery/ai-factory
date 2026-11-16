using Factory.Runner;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>runner.log, and in a container stdout too (contract, "Cloud mode (deployed)").</summary>
public class RunnerLogTests : CommandTestBase
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-16T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Writes_a_timestamped_line_and_echoes_it_to_stdout_only_in_a_container()
    {
        var stdout = new StringWriter { NewLine = "\n" };

        new RunnerLog(Paths, Files, _clock, Options.Create(new RunnerOptions()), stdout).Write("take J-001");
        new RunnerLog(Paths, Files, _clock, Options.Create(new RunnerOptions { Echo = true }), stdout).Write("exit one");

        Assert.Equal($"2026-11-16T10:00:00.0000000Z take J-001{Environment.NewLine}2026-11-16T10:00:00.0000000Z exit one{Environment.NewLine}",
            Files.Text(Path.Combine(Home, "runner.log")));
        Assert.Equal("runner 2026-11-16T10:00:00.0000000Z exit one\n", stdout.ToString());
    }
}
