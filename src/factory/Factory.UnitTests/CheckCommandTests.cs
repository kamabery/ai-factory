using Factory.Cli;

namespace Factory.UnitTests;

/// <summary><c>factory check</c>: a standalone command reading one file from the current directory, no board.</summary>
public class CheckCommandTests
{
    private static readonly string Cwd = Path.Combine(Path.GetTempPath(), "job");
    private readonly MemoryFiles _files = new();

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var host = new CliEnvironment(new Dictionary<string, string?>(), Cwd, "\n", "/appdata", "/tmp", "/bin");
        var (o, e) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        var code = new CheckCommand(host, _files).Run(args, TextReader.Null, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Is_named_check()
    {
        Assert.Equal("check", new CheckCommand(new CliEnvironment(new Dictionary<string, string?>(), "w", "\n", "a", "t", "b"), _files).Name);
    }

    [Fact]
    public void A_file_that_passes_prints_nothing_and_exits_0()
    {
        _files.Put(Path.Combine(Cwd, "docs", "a.md"), "# Title\n");

        Assert.Equal((0, "", ""), Run("file", "docs/a.md", "--heading", "title"));
    }

    [Fact]
    public void A_failed_check_prints_its_line_on_stdout_and_exits_1()
    {
        Assert.Equal((1, "check failed: docs/a.md does not exist\n", ""), Run("file", "docs/a.md"));
    }

    [Fact]
    public void An_absolute_path_is_read_as_given()
    {
        var abs = Path.Combine(Path.GetTempPath(), "elsewhere.md");
        _files.Put(abs, "hello");

        Assert.Equal(0, Run("file", abs, "--contains", "HELLO").Code);
    }

    [Fact]
    public void A_usage_error_prints_the_refusal_on_stderr_and_exits_2()
    {
        Assert.Equal((2, "", "check refused: no path to check\n"), Run("file"));
    }
}
