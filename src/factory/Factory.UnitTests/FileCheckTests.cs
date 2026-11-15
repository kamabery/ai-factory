using Factory.Core;

namespace Factory.UnitTests;

/// <summary><c>factory check file</c>'s rules (contract, "factory commands"): what a gate checks a file for, and how it refuses.</summary>
public class FileCheckTests
{
    private const string Adr = "# ADR 001\n\nIntro line.\n\n## Decision\nWe use Postgres 16.\n\n###### Deep note\n#NoSpace\n####### Seven\n";

    private static IReadOnlyList<string> Check(string? text, params string[] args) =>
        FileCheck.Failures(FileCheckRequest.Parse(["file", "docs/adr.md", .. args]), text);

    private static string Refusal(params string[] args) =>
        Assert.Throws<FactoryException>(() => FileCheckRequest.Parse(args)) is var e && e.ExitCode == 2 ? e.Message : $"exit {e.ExitCode}";

    [Fact]
    public void A_missing_file_fails_once_whatever_else_was_asked()
    {
        Assert.Equal(["check failed: docs/adr.md does not exist"], Check(null, "--heading", "x", "--contains", "y"));
    }

    [Fact]
    public void A_file_that_exists_passes_with_no_options()
    {
        Assert.Empty(Check(""));
    }

    [Fact]
    public void Every_check_that_holds_passes_without_case()
    {
        Assert.Empty(Check(Adr, "--heading", "^decision$", "--heading", "ADR \\d+", "--contains", "postgres 16", "--matches", "^we use \\w+"));
    }

    [Theory]
    [InlineData("^Deep note$", true)]     // ###### is a heading
    [InlineData("^Intro", false)]         // a paragraph is not
    [InlineData("NoSpace", false)]        // # needs a space after it
    [InlineData("Seven", false)]          // seven #s is not a heading
    public void A_heading_is_one_to_six_hashes_then_a_space(string re, bool found)
    {
        Assert.Equal(found, Check(Adr, "--heading", re).Count == 0);
    }

    [Fact]
    public void Each_failed_check_is_one_line_in_the_order_given()
    {
        Assert.Equal(
        [
            "check failed: docs/adr.md does not contain 'MySQL'",
            "check failed: docs/adr.md has no heading matching '^Status$'",
            "check failed: docs/adr.md has no line matching '^Rejected:'",
        ], Check(Adr, "--contains", "MySQL", "--heading", "^Decision$", "--heading", "^Status$", "--matches", "^Rejected:"));
    }

    [Fact]
    public void Matches_tests_each_line_on_its_own()
    {
        Assert.Single(Check("a\nb\n", "--matches", "a\\nb"));
        Assert.Empty(Check("a\r\nb\r\n", "--matches", "^b$"));
    }

    [Theory]
    [InlineData(new string[0], "check refused: usage: factory check file <path> [--heading <re>]... [--contains <text>]... [--matches <re>]...")]
    [InlineData(new[] { "dir", "x" }, "check refused: unknown check 'dir' (only 'file')")]
    [InlineData(new[] { "file" }, "check refused: no path to check")]
    [InlineData(new[] { "file", "a", "b" }, "check refused: unexpected argument 'b'")]
    [InlineData(new[] { "file", "a", "--bogus", "x" }, "check refused: unknown option '--bogus'")]
    [InlineData(new[] { "file", "a", "--heading" }, "check refused: --heading needs a value")]
    [InlineData(new[] { "file", "a", "--matches", "(" }, "check refused: --matches '(' is not a valid regex")]
    public void A_usage_error_is_refused_with_exit_2(string[] args, string message)
    {
        Assert.StartsWith(message, Refusal(args));
    }

    [Fact]
    public void Options_are_read_without_case_and_a_value_may_start_with_a_dash()
    {
        Assert.Empty(Check("x --flag y\n", "--CONTAINS", "--flag"));
    }
}
