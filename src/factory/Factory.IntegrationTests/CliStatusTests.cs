using Factory.Cli;

namespace Factory.IntegrationTests;

/// <summary>The CLI end to end, in process: real composition, real files, exact stdout, stderr and exit code.</summary>
public class CliStatusTests
{
    private static (int Code, string Out, string Err) Run(string cwd, Dictionary<string, string?> env, params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\r\n" };
        var stderr = new StringWriter { NewLine = "\r\n" };
        var code = CliHost.Run(args, env, cwd, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Status_prints_the_board_and_creates_the_state_folder()
    {
        using var dir = new TempDir();
        var home = dir["state"];
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "board.json"), """
            {
              "version": 2,
              "jobs": [
                { "id": "J-001", "stage": "backlog", "round": 1, "title": "Search — fast", "flag": null }
              ],
              "ideas": [ { "id": "I-001", "status": "drafting", "text": "find books" } ]
            }
            """);

        var r = Run(dir.Path, new() { ["FACTORY_HOME"] = home }, "status");

        Assert.Equal((0, "I-001  idea:drafting find books\r\nJ-001  backlog    r1 Search — fast\r\n", ""), r);
    }

    [Fact]
    public void Status_Json_with_no_board_prints_the_empty_board_and_creates_FACTORY_HOME()
    {
        using var dir = new TempDir();
        var home = dir["new-state"];

        var r = Run(dir.Path, new() { ["FACTORY_HOME"] = home }, "status", "-Json");

        Assert.Equal((0, "{\r\n  \"version\": 2,\r\n  \"ideas\": [],\r\n  \"jobs\": []\r\n}\r\n", ""), r);
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public void Outside_a_repository_with_no_FACTORY_HOME_it_refuses()
    {
        using var dir = new TempDir();

        var r = Run(dir.Path, new(), "status");

        Assert.Equal((1, "", "factory: not inside a git repository\r\n"), r);
    }
}
