using Factory.Cli;
using Factory.Core.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>init's browser: FACTORY_BROWSER_CMD names a program and its leading arguments, the URL after them.</summary>
public class SystemBrowserTests
{
    [Fact]
    public void FACTORY_BROWSER_CMD_runs_its_program_with_the_URL_last_and_no_shell()
    {
        var processes = Substitute.For<IProcessRunner>();
        var host = new CliEnvironment(new Dictionary<string, string?> { ["FACTORY_BROWSER_CMD"] = """["dotnet", "th.dll", "tool", "--"]""" }, "w", "\n", "a", "t", "b");

        new SystemBrowser(host, processes).Open("http://127.0.0.1:5000/");

        processes.Received(1).Run("dotnet", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "th.dll", "tool", "--", "http://127.0.0.1:5000/" })), "");
    }
}
