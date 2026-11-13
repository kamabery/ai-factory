using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary>A child on this console (factory serve, runner, idea): its exit code, its directory and its own environment.</summary>
public sealed class ProcessRunnerAttachedTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Returns_the_childs_exit_code_and_runs_it_in_the_directory_with_the_environment_given()
    {
        var repo = Directory.CreateDirectory(_dir["repo"]).FullName;
        var runner = new ProcessRunner();
        Assert.Equal(0, runner.Run("git", ["init", "-q"], repo).ExitCode);
        var outside = Directory.CreateDirectory(_dir["outside"]).FullName;

        // Not a repository: git exits 128. GIT_DIR, set for this child only, makes it one.
        Assert.Equal(128, runner.RunAttached("git", ["rev-parse", "--git-dir"], outside, null));
        Assert.Equal(0, runner.RunAttached("git", ["rev-parse", "--git-dir"], outside, new Dictionary<string, string?> { ["GIT_DIR"] = Path.Combine(repo, ".git") }));
        Assert.Equal(0, runner.RunAttached("git", ["rev-parse", "--git-dir"], repo, null));
    }
}
