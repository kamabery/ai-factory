using Factory.Core.Abstractions;
using Factory.Runner;
using Factory.Runner.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class FactoryScriptTests
{
    [Fact]
    public void A_command_runs_as_dotnet_with_the_CLI_and_its_stderr_on_one_line()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.CliDll.Returns("factory.dll");
        var processes = Substitute.For<IProcessRunner>();
        processes.Run("dotnet", Arg.Any<IReadOnlyList<string>>(), "").Returns(new ProcessResult(1, " J-001\n", "factory: checkout refused:\n  no repo\n"));

        var r = new FactoryScript(paths, processes).Run("checkout", "J-001");

        Assert.Equal(new FactoryResult(1, "J-001", "factory: checkout refused: no repo"), r);
        processes.Received(1).Run("dotnet", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "factory.dll", "checkout", "J-001" })), "");
    }
}
