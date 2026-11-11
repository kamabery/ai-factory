using Factory.Core;

namespace Factory.UnitTests;

/// <summary>The Core helpers spec 06 ports: ConvertTo-Json's doubles, the run mode's provider and Quote-NativeArgs.</summary>
public class GitHelpersTests
{
    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(1.5, "1.5")]
    [InlineData(0.00001, "1E-05")]
    [InlineData(12345678.5, "12345678.5")]
    [InlineData(-2, "-2.0")]
    public void A_double_is_written_as_ConvertTo_Json_writes_it(double value, string json) =>
        Assert.Equal(json, PsNumber.Double(value).ToJsonString());

    [Fact]
    public void A_double_JSON_cannot_hold_refuses() =>
        Assert.Throws<FactoryException>(() => PsNumber.Double(double.NaN));

    [Theory]
    [InlineData(null, "subscription")]
    [InlineData("", "subscription")]
    [InlineData("local", "subscription")]
    [InlineData(" Cloud ", "foundry")]
    public void The_run_mode_decides_the_provider(string? mode, string provider) => Assert.Equal(provider, RunMode.Provider(mode));

    [Fact]
    public void An_unknown_run_mode_refuses() =>
        Assert.Equal("FACTORY_MODE 'shelf' must be local or cloud", Assert.Throws<FactoryException>(() => RunMode.Provider("SHELF")).Message);

    [Theory]
    [InlineData("terraform plan -var-file=envs/ephemeral.tfvars", "terraform plan \"-var-file=envs/ephemeral.tfvars\"")]
    [InlineData("cmd /c echo -q=a.b; exit 0", "cmd /c echo \"-q=a.b\"; exit 0")]
    [InlineData("(tool -x=a.b)", "(tool \"-x=a.b\")")]
    [InlineData("tool -x=ab", "tool -x=ab")]
    [InlineData("tool '-x=a.b'", "tool '-x=a.b'")]
    [InlineData("dotnet test x.slnx", "dotnet test x.slnx")]
    public void A_native_name_value_argument_with_a_dot_is_quoted(string cmd, string quoted) => Assert.Equal(quoted, NativeArgs.Quote(cmd));
}
