using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

public class FactoryPathsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fx"));

    private static FactoryPaths Paths(IGitClient git, string? home = null, string? repo = null, string? envFile = null) =>
        new(Options.Create(new FactoryOptions { Home = home, Repo = repo, EnvFile = envFile, WorkingDirectory = Path.Combine(Root, "cwd") }), git);

    [Theory, AutoNSubData]
    public void FACTORY_HOME_wins_and_git_is_not_asked(IGitClient git)
    {
        var home = Path.Combine(Root, "state");

        var paths = Paths(git, home: home, repo: Path.Combine(Root, "repo"));

        Assert.Equal(home, paths.Home);
        Assert.Equal(Path.Combine(home, "board.json"), paths.BoardPath);
        Assert.Equal(Path.Combine(home, "events.jsonl"), paths.EventsPath);
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

    [Theory, AutoNSubData]
    public void The_env_file_is_FACTORY_ENV_FILE_else_the_main_checkouts(IGitClient git)
    {
        Assert.Equal("x.env", Paths(git, envFile: "x.env").EnvFile);
        Assert.Equal(Path.Combine(Root, "repo", ".env"), Paths(git, repo: Path.Combine(Root, "repo")).EnvFile);
        git.DidNotReceiveWithAnyArgs().CommonDir(default!);
    }

    [Theory, AutoNSubData]
    public void Outside_a_repository_there_is_no_env_file(IGitClient git)
    {
        git.CommonDir(Arg.Any<string>()).Returns((string?)null);

        Assert.Null(Paths(git, home: Path.Combine(Root, "state")).EnvFile);
    }

    [Theory, AutoNSubData]
    public void The_CLI_runner_and_server_are_built_beside_this_host_in_its_configuration(IGitClient git)
    {
        var bin = Path.Combine(Root, "artifacts", "bin");
        var p = new FactoryPaths(Options.Create(new FactoryOptions { BaseDirectory = Path.Combine(bin, "Factory.Server", "release") + Path.DirectorySeparatorChar }), git);

        Assert.Equal(Path.Combine(bin, "Factory.Cli", "release", "factory.dll"), p.CliDll);
        Assert.Equal(Path.Combine(bin, "Factory.Runner", "release", "Factory.Runner.dll"), p.RunnerDll);
        Assert.Equal(Path.Combine(bin, "Factory.Server", "release", "Factory.Server.dll"), p.ServerDll);
    }

    [Theory, AutoNSubData]
    public void FACTORY_CLI_RUNNER_and_SERVER_win(IGitClient git)
    {
        var p = new FactoryPaths(Options.Create(new FactoryOptions { BaseDirectory = Root, Cli = "c.dll", Runner = "r.dll", Server = "s.dll" }), git);

        Assert.Equal(("c.dll", "r.dll", "s.dll"), (p.CliDll, p.RunnerDll, p.ServerDll));
    }
}
