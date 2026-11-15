using Factory.Core;

namespace Factory.UnitTests;

/// <summary>A test override (FACTORY_GH_CMD and the rest) names a program and its leading arguments (contract, "Implementations").</summary>
public class CommandOverrideTests
{
    [Fact]
    public void A_JSON_array_is_the_program_then_its_leading_arguments() =>
        Assert.Equal(["dotnet", "C:/a b/Factory.TestHost.dll", "tool", "--log", "x.jsonl", "--"],
            CommandOverride.Parse("FACTORY_GH_CMD", """ ["dotnet", "C:/a b/Factory.TestHost.dll", "tool", "--log", "x.jsonl", "--"] """));

    [Theory]
    [InlineData(@"C:\tools\fake gh.exe")]
    [InlineData("/usr/local/bin/fake-gh")]
    [InlineData("fake-gh")]
    public void Anything_else_is_one_program_spaces_and_all(string value) =>
        Assert.Equal([value], CommandOverride.Parse("FACTORY_GH_CMD", value));

    [Theory]
    [InlineData("[]")]
    [InlineData("[1, 2]")]
    [InlineData("[\"dotnet\", null]")]
    [InlineData("[\"\"]")]
    [InlineData("[not json")]
    public void A_bad_array_is_refused_naming_the_variable(string value) =>
        Assert.Equal("FACTORY_AZ_CMD must be a JSON array of strings, the program first, or a path",
            Assert.Throws<FactoryException>(() => CommandOverride.Parse("FACTORY_AZ_CMD", value)).Message);
}
