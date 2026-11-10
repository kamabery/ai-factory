using AutoFixture.Xunit3;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

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
