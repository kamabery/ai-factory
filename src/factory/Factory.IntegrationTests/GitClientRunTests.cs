using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary>factory.ps1's Invoke-Git: git -C, the exit code, and the output trimmed.</summary>
public sealed class GitClientRunTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GitClient _git = new(new ProcessRunner());

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Runs_git_in_the_folder_and_returns_its_exit_code_and_trimmed_output()
    {
        var repo = new GitRepo(_dir["repo"]);
        var sha = repo.Commit("first", ("a.txt", "1")).Trim();

        Assert.Equal((0, sha), (_git.Run(repo.Path, "rev-parse", "HEAD").Code, _git.Run(repo.Path, "rev-parse", "HEAD").Out));
        Assert.Equal(1, _git.Run(repo.Path, "rev-parse", "--verify", "--quiet", "refs/remotes/factory/base").Code);
    }

    [Fact]
    public void Git_never_prompts_for_a_credential()
    {
        Assert.Equal("0", _git.Run(_dir.Path, "-c", "alias.p=!echo $GIT_TERMINAL_PROMPT", "p").Out);
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_gits_error_not_an_exception()
    {
        var r = _git.Run(_dir["missing"], "status");

        Assert.NotEqual(0, r.Code);
        Assert.Contains("cannot change to", r.Out);
    }
}
