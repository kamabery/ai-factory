using AutoFixture.Xunit3;
using Factory.Cli;
using Factory.Core;
using Factory.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

public class CommandArgsTests
{
    [Fact]
    public void No_arguments_is_status()
    {
        var a = CommandArgs.Parse([]);

        Assert.Equal("status", a.Command);
        Assert.Empty(a.Positional);
    }

    [Fact]
    public void Reads_positionals_named_values_and_switches_case_insensitively()
    {
        var a = CommandArgs.Parse(["set-stage", "J-001", "build", "-by", "runner", "-JSON"]);

        Assert.Equal("set-stage", a.Command);
        Assert.Equal(["J-001", "build"], a.Positional);
        Assert.Equal("runner", a.Get("By"));
        Assert.True(a.Has("Json"));
        Assert.True(a.Has("by"));
        Assert.False(a.Has("Force"));
        Assert.Null(a.Get("Reason"));
    }

    [Fact]
    public void A_named_parameter_needs_a_value()
    {
        var e = Assert.Throws<FactoryException>(() => CommandArgs.Parse(["approve", "J-001", "-By"]));
        Assert.Equal("missing a value for -By", e.Message);
    }
}

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

public class StatusCommandTests
{
    [Theory, AutoNSubData]
    public void Prints_the_text_report([Frozen] IBoardReader board, StatusCommand sut)
    {
        board.Load().Returns(Board.Empty());

        Assert.Equal(CommandResult.Ok("board is empty").Out, sut.Run(CommandArgs.Parse(["status"])).Out);
    }

    [Theory, AutoNSubData]
    public void Json_prints_the_board_as_one_block_with_the_platform_newline(
        [Frozen] IBoardReader board, [Frozen] IOptions<CliOptions> options, StatusCommand sut)
    {
        board.Load().Returns(Board.Empty());
        options.Value.Returns(new CliOptions { NewLine = "\r\n" });

        var r = sut.Run(CommandArgs.Parse(["status", "-Json"]));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["{\r\n  \"version\": 2,\r\n  \"ideas\": [],\r\n  \"jobs\": []\r\n}"], r.Out);
    }
}

public class CompositionTests
{
    [Fact]
    public void The_container_builds_validated_and_resolves_every_service()
    {
        var services = new ServiceCollection().AddFactoryCli(new Dictionary<string, string?>(), "/", "\n");

        using var provider = Composition.Build(services);

        foreach (var d in services.Where(d => !d.ServiceType.IsGenericTypeDefinition))
            Assert.NotNull(provider.GetRequiredService(d.ServiceType));
        Assert.Contains(provider.GetServices<ICommand>(), c => c.Name == "status");
    }

    [Fact]
    public void Binds_the_factory_settings_from_the_environment()
    {
        var env = new Dictionary<string, string?> { ["FACTORY_HOME"] = "h", ["FACTORY_REPO"] = "r" };

        using var provider = Composition.Build(new ServiceCollection().AddFactoryCli(env, "w", "\r\n"));

        var o = provider.GetRequiredService<IOptions<FactoryOptions>>().Value;
        Assert.Equal(("h", "r", "w"), (o.Home, o.Repo, o.WorkingDirectory));
        Assert.Equal("\r\n", provider.GetRequiredService<IOptions<CliOptions>>().Value.NewLine);
    }
}
