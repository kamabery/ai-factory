using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using NSubstitute;

namespace Factory.UnitTests;

public class CliAppTests
{
    private static ICommand Command(string name, CommandResult result)
    {
        var c = Substitute.For<ICommand>();
        c.Name.Returns(name);
        c.Run(Arg.Any<CommandArgs>()).Returns(result);
        return c;
    }

    [Fact]
    public void Runs_the_named_command_whatever_its_case()
    {
        var status = Command("status", CommandResult.Ok("board is empty"));

        var r = new CliApp([Command("other", CommandResult.Ok()), status]).Run(["STATUS", "-Json"]);

        Assert.Equal(["board is empty"], r.Out);
        status.Received(1).Run(Arg.Is<CommandArgs>(a => a.Has("Json")));
    }

    [Fact]
    public void An_unknown_command_is_refused_as_factory_ps1_refuses_it()
    {
        var r = new CliApp([]).Run(["frobnicate"]);

        Assert.Empty(r.Out);
        Assert.Equal(["factory: unknown command 'frobnicate'"], r.Err);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void A_refusal_becomes_stderr_and_its_exit_code()
    {
        var c = Substitute.For<ICommand>();
        c.Name.Returns("status");
        c.Run(Arg.Any<CommandArgs>()).Returns(_ => throw new FactoryException("not inside a git repository", 3));

        var r = new CliApp([c]).Run([]);

        Assert.Equal(3, r.ExitCode);
        Assert.Empty(r.Out);
        Assert.Equal(["factory: not inside a git repository"], r.Err);
    }
}
