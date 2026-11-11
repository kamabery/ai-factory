using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

public class CliAppTests
{
    private readonly IFactoryStore _store = Substitute.For<IFactoryStore>();
    private readonly IBoardSession _session = Substitute.For<IBoardSession>();

    public CliAppTests()
    {
        _store.Open(Arg.Any<bool>()).Returns(_session);
        _session.Close().Returns([]);
    }

    private static ICommand Command(string name, CommandResult result, bool refreshes = true)
    {
        var c = Substitute.For<ICommand>();
        c.Name.Returns(name);
        c.RefreshesBoard.Returns(refreshes);
        c.Run(Arg.Any<CommandArgs>(), Arg.Any<IBoardSession>()).Returns(result);
        return c;
    }

    [Fact]
    public void Opens_the_board_unrefreshed_for_a_command_that_says_so()
    {
        new CliApp([Command("state-migrate", CommandResult.Ok(), refreshes: false)], _store).Run(["state-migrate"]);

        _store.Received(1).Open(false);
    }

    [Fact]
    public void Runs_the_named_command_whatever_its_case_on_the_open_board_then_closes_it()
    {
        var status = Command("status", CommandResult.Ok("board is empty"));

        var r = new CliApp([Command("other", CommandResult.Ok()), status], _store).Run(["STATUS", "-Json"]);

        Assert.Equal(["board is empty"], r.Out);
        Received.InOrder(() =>
        {
            _store.Open();
            status.Run(Arg.Is<CommandArgs>(a => a.Has("Json")), _session);
            _session.Close();
            _session.Dispose();
        });
    }

    [Fact]
    public void An_unknown_command_is_refused_as_factory_ps1_refuses_it()
    {
        var r = new CliApp([], _store).Run(["frobnicate"]);

        Assert.Empty(r.Out);
        Assert.Equal(["factory: unknown command 'frobnicate'"], r.Err);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void A_refusal_becomes_stderr_and_its_exit_code_and_the_board_is_still_closed()
    {
        var c = Substitute.For<ICommand>();
        c.Name.Returns("status");
        c.Run(Arg.Any<CommandArgs>(), Arg.Any<IBoardSession>()).Throws(new FactoryException("no job J-009", 3));
        _session.Close().Returns(["factory: state push failed: x"]);

        var r = new CliApp([c], _store).Run([]);

        Assert.Equal(3, r.ExitCode);
        Assert.Empty(r.Out);
        Assert.Equal(["factory: no job J-009", "factory: state push failed: x"], r.Err);
        _session.Received(1).Dispose();
    }

    [Fact]
    public void A_board_that_cannot_be_opened_is_a_refusal()
    {
        _store.Open().Throws(new FactoryException("board lock refused: another factory.ps1 has held the board for over 30 s"));

        var r = new CliApp([Command("status", CommandResult.Ok())], _store).Run([]);

        Assert.Equal((1, "factory: board lock refused: another factory.ps1 has held the board for over 30 s"), (r.ExitCode, r.Err.Single()));
    }
}
