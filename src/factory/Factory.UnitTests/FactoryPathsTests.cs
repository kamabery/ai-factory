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

    // --- installs (contract, "Installs: a checkout or the dotnet tool") ---------------------------------------------

    private static readonly string Tool = Path.Combine(Root, "store", "ai-factory", "tools") + Path.DirectorySeparatorChar;

    private static FactoryPaths ToolPaths(IGitClient git, Action<FactoryOptions>? more = null)
    {
        var o = new FactoryOptions
        {
            BaseDirectory = Tool, Scripts = Path.Combine(Tool, "factory", "scripts"), UserHome = Path.Combine(Root, "user"),
            WorkingDirectory = Path.Combine(Root, "some-target"),
        };
        more?.Invoke(o);
        return new FactoryPaths(Options.Create(o), git);
    }

    [Theory, AutoNSubData]
    public void A_factory_root_in_the_hosts_own_folder_is_a_tool_install(IGitClient git)
    {
        var bin = Path.Combine(Root, "repo", "artifacts", "bin", "Factory.Cli", "debug") + Path.DirectorySeparatorChar;
        var checkout = new FactoryPaths(Options.Create(new FactoryOptions { BaseDirectory = bin, Scripts = Path.Combine(Root, "repo", "factory", "scripts") }), git);

        Assert.True(ToolPaths(git).IsToolInstall);
        Assert.False(checkout.IsToolInstall);
    }

    [Theory, AutoNSubData]
    public void A_tool_install_keeps_state_env_and_templates_in_the_profile_wherever_it_runs(IGitClient git)
    {
        git.CommonDir(Arg.Any<string>()).Returns(Path.Combine(Root, "some-target", ".git"));
        var profile = Path.Combine(Root, "user", ".ai-factory");

        var p = ToolPaths(git);

        Assert.Equal(profile, p.Profile);
        Assert.Equal(Path.Combine(profile, "state"), p.Home);
        Assert.Equal(Path.Combine(profile, ".env"), p.EnvFile);
        Assert.Equal(Path.Combine(profile, "template-projects"), p.TemplatesRoot);
        Assert.Equal(Path.Combine(Tool, ".env.example"), p.EnvExample);
        git.DidNotReceiveWithAnyArgs().CommonDir(default!);
    }

    [Theory, AutoNSubData]
    public void FACTORY_PROFILE_moves_the_profile_and_the_explicit_settings_still_win(IGitClient git)
    {
        var p = ToolPaths(git, o => o.Profile = Path.Combine(Root, "p2"));
        var q = ToolPaths(git, o => { o.Home = "h"; o.EnvFile = "e.env"; o.Templates = "t"; });

        Assert.Equal(Path.Combine(Root, "p2", "state"), p.Home);
        Assert.Equal(("h", "e.env", "t"), (q.Home, q.EnvFile, q.TemplatesRoot));
    }

    [Theory, AutoNSubData]
    public void A_tool_install_runs_the_runner_and_server_beside_the_CLI(IGitClient git)
    {
        var p = ToolPaths(git);

        Assert.Equal(Path.Combine(Tool, "factory.dll"), p.CliDll);
        Assert.Equal(Path.Combine(Tool, "Factory.Runner.dll"), p.RunnerDll);
        Assert.Equal(Path.Combine(Tool, "Factory.Server.dll"), p.ServerDll);
    }

    [Theory, AutoNSubData]
    public void The_agents_factory_command_is_the_global_tools_folder_in_a_tool_install_else_the_CLIs_build(IGitClient git)
    {
        var bin = Path.Combine(Root, "artifacts", "bin");
        var checkout = new FactoryPaths(Options.Create(new FactoryOptions { BaseDirectory = Path.Combine(bin, "Factory.Runner", "debug") + Path.DirectorySeparatorChar }), git);

        Assert.Equal(Path.Combine(bin, "Factory.Cli", "debug"), checkout.CommandDir);
        Assert.Equal(Path.Combine(Root, "user", ".dotnet", "tools"), ToolPaths(git).CommandDir);
        Assert.Equal(Path.Combine(Root, "cli-home", ".dotnet", "tools"), ToolPaths(git, o => o.DotnetCliHome = Path.Combine(Root, "cli-home")).CommandDir);
    }

    [Theory, AutoNSubData]
    public void A_checkout_install_has_no_profile_and_its_env_example_sits_beside_the_env(IGitClient git)
    {
        var p = Paths(git, repo: Path.Combine(Root, "repo"));

        Assert.Null(p.Profile);
        Assert.Equal(Path.Combine(Root, "repo", ".env.example"), p.EnvExample);
    }
}
