using Factory.Cli;
using Factory.Core;

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
    public void A_dash_that_is_not_a_factory_ps1_parameter_is_a_positional_value()
    {
        var a = CommandArgs.Parse(["rework", "J-001", "-x keeps working\nScope +: a/**", "-Detail", "-d"]);

        Assert.Equal(["J-001", "-x keeps working\nScope +: a/**"], a.Positional);
        Assert.Equal("-d", a.Get("Detail"));
        Assert.Equal(("J-001", "", "-d", ""), (a.Arg(1), a.Arg(3), a.Named("Detail"), a.Named("By")));
    }

    [Fact]
    public void A_named_parameter_needs_a_value()
    {
        var e = Assert.Throws<FactoryException>(() => CommandArgs.Parse(["approve", "J-001", "-By"]));
        Assert.Equal("missing a value for -By", e.Message);
    }
}
