using Factory.Core;

namespace Factory.UnitTests;

/// <summary>A gate line is a program and its arguments, run with no shell (contract, "Gate").</summary>
public class GateLineTests
{
    private const string Check = "use factory check file";
    private const string Dir = "use the tool's directory flag or path";
    private const string Split = "use one gate line per command, or a script the target commits";

    [Theory]
    [InlineData("dotnet build", new[] { "dotnet", "build" })]
    [InlineData("  dotnet\ttest   --filter \"Category=Unit\"  ", new[] { "dotnet", "test", "--filter", "Category=Unit" })]
    [InlineData("tool --x='a b'c", new[] { "tool", "--x=a bc" })]
    [InlineData(@"C:\tools\x.exe src\a.md", new[] { @"C:\tools\x.exe", @"src\a.md" })]
    [InlineData("factory check file a.md --matches \"^(a|b)$\"", new[] { "factory", "check", "file", "a.md", "--matches", "^(a|b)$" })]
    [InlineData("tool \"\" 'it''s'", new[] { "tool", "", "its" })]
    [InlineData("terraform plan -var-file=envs/ephemeral.tfvars", new[] { "terraform", "plan", "-var-file=envs/ephemeral.tfvars" })]
    public void Splits_at_spaces_keeping_quoted_text_whole_and_dropping_the_quotes(string line, string[] args)
    {
        Assert.Null(GateLine.Problem(line));
        Assert.Equal(args, GateLine.Split(line));
    }

    [Theory]
    [InlineData("aspire-addon infra check")]
    [InlineData("npm --prefix src/ui/react run build")]
    [InlineData("terraform -chdir=IaC validate")]
    [InlineData("git --version")]
    public void A_program_with_arguments_is_no_shell_line(string line) => Assert.Null(GateLine.Problem(line));

    [Theory]
    [InlineData("Set-Location IaC; terraform init", "`Set-Location` is a PowerShell cmdlet", Dir)]
    [InlineData("Push-Location src", "`Push-Location` is a PowerShell cmdlet", Dir)]
    [InlineData("cd src", "`cd` is a shell builtin", Dir)]
    [InlineData("Test-Path README.md", "`Test-Path` is a PowerShell cmdlet", Check)]
    [InlineData("Select-String -Path a.md -Pattern x", "`Select-String` is a PowerShell cmdlet", Check)]
    [InlineData("Get-Content a.md", "`Get-Content` is a PowerShell cmdlet", Check)]
    [InlineData("Get-Item a.md", "`Get-Item` is a PowerShell cmdlet", Check)]
    [InlineData("Get-ChildItem src", "`Get-ChildItem` is a PowerShell cmdlet", Check)]
    [InlineData("Write-Host hi", "`Write-Host` is a PowerShell cmdlet", Split)]
    [InlineData("exit 0", "`exit` is a shell builtin", Split)]
    [InlineData("echo $HOME", "`echo` is a shell builtin", Split)]
    [InlineData("TRUE", "`TRUE` is a shell builtin", Split)]
    [InlineData("dotnet build; dotnet test", "`;` needs a shell", Split)]
    [InlineData("npm test | tee out.txt", "`|` needs a shell", Split)]
    [InlineData("npm test && npm run lint", "`&` needs a shell", Split)]
    [InlineData("dotnet test > out.txt", "`>` needs a shell", Split)]
    [InlineData("tool < in.txt", "`<` needs a shell", Split)]
    [InlineData("dotnet test $x", "`$` needs a shell", Split)]
    [InlineData("tool `x`", "``` needs a shell", Split)]
    [InlineData("(dotnet build)", "`(` needs a shell", Split)]
    [InlineData("tool {a}", "`{` needs a shell", Split)]
    [InlineData("dotnet \"unterminated", "an unterminated quote", Split)]
    public void A_shell_line_says_why_and_how_to_rewrite_it(string line, string reason, string hint)
    {
        Assert.Equal(new GateLineProblem(reason, hint), GateLine.Problem(line));
    }

    [Fact]
    public void An_empty_line_is_a_problem() => Assert.Equal(new GateLineProblem("no program", Split), GateLine.Problem("   "));

    [Fact]
    public void A_name_value_argument_is_one_argument() =>
        Assert.Equal(["tool", "-x=a.b"], GateLine.Split("tool -x=a.b"));
}
