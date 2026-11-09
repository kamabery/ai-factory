using AutoFixture.Xunit3;
using Factory.Core;
using Factory.Store;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

public class FactoryPathsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fx"));

    private static FactoryPaths Paths(IGitClient git, string? home = null, string? repo = null) =>
        new(Options.Create(new FactoryOptions { Home = home, Repo = repo, WorkingDirectory = Path.Combine(Root, "cwd") }), git);

    [Theory, AutoNSubData]
    public void FACTORY_HOME_wins_and_git_is_not_asked(IGitClient git)
    {
        var home = Path.Combine(Root, "state");

        var paths = Paths(git, home: home, repo: Path.Combine(Root, "repo"));

        Assert.Equal(home, paths.Home);
        Assert.Equal(Path.Combine(home, "board.json"), paths.BoardPath);
        git.DidNotReceiveWithAnyArgs().CommonDir(default!);
    }

    [Theory, AutoNSubData]
    public void FACTORY_REPO_gives_its_factory_runs(IGitClient git)
    {
        var repo = Path.Combine(Root, "repo");

        Assert.Equal(Path.Combine(repo, ".factory-runs"), Paths(git, repo: repo).Home);
        git.DidNotReceiveWithAnyArgs().CommonDir(default!);
    }

    [Theory, AutoNSubData]
    public void Otherwise_the_main_checkout_is_the_git_common_dirs_parent(IGitClient git)
    {
        // A linked worktree's common dir is the main checkout's .git, so state lands in the main checkout.
        git.CommonDir(Path.Combine(Root, "cwd")).Returns(Root.Replace('\\', '/') + "/main/.git");

        Assert.Equal(Path.Combine(Root, "main", ".factory-runs"), Paths(git).Home);
    }

    [Theory, AutoNSubData]
    public void Empty_settings_count_as_unset(IGitClient git)
    {
        git.CommonDir(Arg.Any<string>()).Returns(Path.Combine(Root, "main", ".git"));

        Assert.Equal(Path.Combine(Root, "main", ".factory-runs"), Paths(git, home: "", repo: "").Home);
    }

    [Theory, AutoNSubData]
    public void Outside_a_repository_it_refuses(IGitClient git)
    {
        git.CommonDir(Arg.Any<string>()).Returns((string?)null);

        var e = Assert.Throws<FactoryException>(() => Paths(git).Home);
        Assert.Equal("not inside a git repository", e.Message);
    }
}
