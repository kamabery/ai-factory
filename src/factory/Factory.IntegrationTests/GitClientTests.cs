using Factory.Store;

namespace Factory.IntegrationTests;

public class GitClientTests
{
    private static readonly GitClient Git = new(new ProcessRunner());

    [Fact]
    public void Finds_the_main_checkouts_git_dir_from_a_linked_worktree()
    {
        using var dir = new TempDir();
        var main = dir["main"];
        var runner = new ProcessRunner();
        Directory.CreateDirectory(main);
        Run(runner, main, "init", "-q");
        Run(runner, main, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init");
        Run(runner, main, "worktree", "add", "-q", dir["wt"]);

        var common = Git.CommonDir(dir["wt"]);

        Assert.NotNull(common);
        Assert.Equal(Path.GetFullPath(Path.Combine(main, ".git")), Path.GetFullPath(common));
    }

    [Fact]
    public void Outside_a_repository_is_null()
    {
        using var dir = new TempDir();

        Assert.Null(Git.CommonDir(dir.Path));
    }

    [Fact]
    public void A_process_that_fails_reports_its_exit_code_and_stderr()
    {
        using var dir = new TempDir();

        var r = new ProcessRunner().Run("git", ["no-such-command"], dir.Path);

        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("no-such-command", r.StdErr);
    }

    private static void Run(ProcessRunner runner, string cwd, params string[] args)
    {
        var r = runner.Run("git", args, cwd);
        Assert.True(r.ExitCode == 0, r.StdErr);
    }
}
