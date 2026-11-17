using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>factory runner, serve and idea: the processes a human starts on their own console.</summary>
public class HumanCommandsTests
{
    private static readonly string Repo = Path.Combine(Path.GetTempPath(), "fac");
    private readonly IFactoryPaths _paths = Substitute.For<IFactoryPaths>();
    private readonly MemoryFiles _files = new();
    private readonly IProcessRunner _processes = Substitute.For<IProcessRunner>();
    private readonly IPause _pause = Substitute.For<IPause>();
    private readonly IDetachedCommands _detached = Substitute.For<IDetachedCommands>();
    private List<(string File, string[] Args, string Dir, Dictionary<string, string?> Env)> Attached { get; } = [];

    public HumanCommandsTests()
    {
        _paths.ScriptsDir.Returns(Path.Combine(Repo, "factory", "scripts"));
        _paths.CliDll.Returns(Path.Combine(Repo, "bin", "cli", "factory.dll"));
        _paths.CommandDir.Returns(Path.Combine(Repo, "bin", "cli"));
        _paths.RunnerDll.Returns(Path.Combine(Repo, "bin", "runner", "Factory.Runner.dll"));
        _paths.ServerDll.Returns(Path.Combine(Repo, "bin", "server", "Factory.Server.dll"));
        _paths.WorkRoot.Returns(Path.Combine(Repo, "work"));
        _processes.RunAttached(default!, default!, default!, default).ReturnsForAnyArgs(c =>
        {
            Attached.Add((c.ArgAt<string>(0), [.. c.ArgAt<IReadOnlyList<string>>(1)], c.ArgAt<string>(2), new(c.ArgAt<IReadOnlyDictionary<string, string?>?>(3) ?? new Dictionary<string, string?>())));
            return 7;
        });
    }

    private static CliEnvironment Host(Dictionary<string, string?>? env = null) =>
        new(env ?? new() { ["PATH"] = "p" }, "cwd", "\n", "/appdata", "/tmp", "/bin", ProcessId: 42, DotnetRoot: "/dn");

    private (int Code, string Err) Run(IStandaloneCommand command, params string[] args)
    {
        var err = new StringWriter();
        var code = command.Run(args, TextReader.Null, TextWriter.Null, err);
        return (code, err.ToString().Trim());
    }

    [Fact]
    public void Runner_runs_the_runner_on_this_console_in_local_mode_unless_a_mode_is_configured()
    {
        _files.Put(_paths.RunnerDll, "dll");

        Assert.Equal((7, ""), Run(new RunnerCommand(_paths, _files, _processes, Host())));
        Assert.Equal((7, ""), Run(new RunnerCommand(_paths, _files, _processes, Host(new() { ["Factory__Mode"] = "cloud" }))));

        Assert.Equal(("dotnet", "cwd"), (Attached[0].File, Attached[0].Dir));
        Assert.Equal([_paths.RunnerDll], Attached[0].Args);
        Assert.Equal("local", Attached[0].Env["Factory__Mode"]);
        Assert.False(Attached[1].Env.ContainsKey("Factory__Mode"));
    }

    [Fact]
    public void Serve_passes_its_arguments_and_itself_as_the_servers_parent()
    {
        _files.Put(_paths.ServerDll, "dll");

        Assert.Equal((7, ""), Run(new ServeCommand(_paths, _files, _processes, Host()), "--port", "4317"));

        Assert.Equal("dotnet", Attached[0].File);
        Assert.Equal([_paths.ServerDll, "--port", "4317", "--parent-pid", "42"], Attached[0].Args);
    }

    private ContainerCommand Container(Dictionary<string, string?> env) =>
        new(_paths, _processes, _files, _pause, _detached, new TemplateWorkspace(_paths, _files, Substitute.For<IGitClient>(), Substitute.For<IDotEnv>(),
            Substitute.For<IGitHub>(), Substitute.For<IGitHubCredential>()), Host(env));

    private static Dictionary<string, string?> Identity(string? port = null) =>
        new() { ["IDENTITY_ENDPOINT"] = "http://id", ["IDENTITY_HEADER"] = "h", ["AZURE_CLIENT_ID"] = "cid", ["PORT"] = port };

    [Fact]
    public void Container_serve_signs_az_in_as_the_identity_then_serves_on_every_interface_behind_sign_in()
    {
        _processes.Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(0, "[]", ""));

        Assert.Equal((7, ""), Run(Container(Identity("8443")), "serve"));

        _processes.Received(1).Run(StateToken.AzProgram, Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "login", "--identity", "--client-id", "cid", "--output", "none" })), "");
        Assert.Equal([_paths.ServerDll, "--port", "8443", "--require-signin"], Attached[0].Args);
        Assert.Equal("1", Attached[0].Env["FACTORY_CONTAINER"]);
    }

    [Fact]
    public void Container_job_runs_one_job_and_its_exit_code_is_the_containers()
    {
        _processes.Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(0, "", ""));

        Assert.Equal((7, ""), Run(Container(Identity()), "job"));

        Assert.Equal([_paths.RunnerDll, "--one"], Attached[0].Args);
        Assert.Equal("1", Attached[0].Env["FACTORY_CONTAINER"]);
        Assert.Equal((7, ""), Run(Container(Identity()), "serve"));
        Assert.Equal([_paths.ServerDll, "--port", "8080", "--require-signin"], Attached[1].Args);
    }

    [Fact]
    public void Container_writes_the_profiles_env_from_its_environment_without_a_secret_before_it_starts()
    {
        var envFile = Path.Combine(Repo, "profile", ".env");
        _paths.EnvFile.Returns(envFile);
        _files.Put(envFile, "OLD=1\n");
        _processes.Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(0, "", ""));
        var env = Identity();
        env["GITHUB_OWNER"] = "kamabery";
        env["HCP_ORG"] = "org";
        env["FACTORY_MODE"] = "cloud";
        env["HCP_TOKEN"] = "never-written";
        env["GITHUB_APP_PRIVATE_KEY"] = "never-written";
        env["PATH"] = "/usr/bin";

        Assert.Equal((7, ""), Run(Container(env), "serve"));

        Assert.Equal("AZURE_CLIENT_ID=cid\nFACTORY_MODE=cloud\nGITHUB_OWNER=kamabery\nHCP_ORG=org\n", _files.Text(envFile));
    }

    [Fact]
    public void Container_job_lives_until_the_commands_its_runner_started_detached_have_finished()
    {
        _processes.Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(0, "", ""));
        _detached.AnyRunning().Returns(true, true, false);

        Assert.Equal((7, ""), Run(Container(Identity()), "job"));

        _detached.Received(3).AnyRunning();
        _pause.Received(2).Sleep(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Container_serve_does_not_wait_on_detached_commands()
    {
        _processes.Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(0, "", ""));

        Run(Container(Identity()), "serve");

        _detached.DidNotReceive().AnyRunning();
    }

    [Fact]
    public void Container_tries_az_login_again_while_the_identity_endpoint_starts_up()
    {
        _processes.Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>())
            .Returns(new ProcessResult(1, "", "ERROR: JSON is invalid"), new ProcessResult(0, "", ""));

        Assert.Equal((7, ""), Run(Container(Identity()), "job"));

        _processes.Received(2).Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), "");
        _pause.Received(1).Sleep(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Container_refuses_outside_a_managed_identity_a_failed_az_login_and_a_bad_argument()
    {
        Assert.Equal((1, "factory: container refused: no managed identity (IDENTITY_ENDPOINT is unset)"), Run(Container(new()), "serve"));
        Assert.Equal((2, "factory: container refused: usage: factory container serve|job"), Run(Container(Identity()), "both"));
        _processes.Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(1, "", "ERROR: no identity\n"));
        Assert.Equal((1, "factory: container refused: az login --identity failed: ERROR: no identity"), Run(Container(Identity()), "job"));
        _processes.Received(3).Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), "");
        Assert.Empty(Attached);
    }

    [Fact]
    public void Runner_and_serve_refuse_when_not_built()
    {
        Assert.Equal((1, $"factory: runner refused: the runner is not built at {_paths.RunnerDll} (dotnet build src/factory)"),
            Run(new RunnerCommand(_paths, _files, _processes, Host())));
        Assert.Equal((1, $"factory: serve refused: the dashboard server is not built at {_paths.ServerDll} (dotnet build src/factory)"),
            Run(new ServeCommand(_paths, _files, _processes, Host())));
        Assert.Empty(Attached);
    }

    [Fact]
    public void Idea_on_an_existing_target_opens_claude_on_its_clone_with_this_CLI_first_on_PATH_and_deletes_the_clone()
    {
        var clone = Path.Combine(Repo, "work", "cli", "me-app");
        Cli("check-target", ["me/app"], "existing\n");
        Cli("clone-target", ["me/app", clone], clone + "\n");
        _files.Put(Path.Combine(clone, "README.md"), "x");

        Assert.Equal((7, ""), Run(new IdeaCommand(_paths, _files, _processes, Host()), "me/app"));

        var (file, args, dir, env) = Attached.Single();
        Assert.Equal(ClaudeCli.Executable(), file);
        Assert.Equal(["--plugin-dir", Path.Combine(Repo, "factory", "plugin"), "--add-dir", clone, $"/factory:idea me/app clone={clone}"], args);
        Assert.Equal(Repo, dir);
        Assert.Equal(Path.Combine(Repo, "bin", "cli") + Path.PathSeparator + "p", env["PATH"]);
        Assert.False(env.ContainsKey("FACTORY_IDEA"));
        Assert.Equal(!OperatingSystem.IsWindows(), env.ContainsKey("DOTNET_ROOT"));
        Assert.False(_files.DirectoryExists(clone));
    }

    [Fact]
    public void Idea_resumes_an_idea_with_its_hook_events_going_to_it_and_a_new_target_has_no_clone()
    {
        Cli("status", ["-Json"], """{ "ideas": [ { "id": "I-007", "target": "me/new" } ], "jobs": [] }""");
        Cli("check-target", ["me/new"], "new\n");

        Run(new IdeaCommand(_paths, _files, _processes, Host()), "I-007");

        var (_, args, _, env) = Attached.Single();
        Assert.Equal("I-007", env["FACTORY_IDEA"]);
        Assert.Equal("/factory:idea I-007", args[^1]);
        Assert.DoesNotContain("--add-dir", args);
        _processes.DidNotReceive().Run("dotnet", Arg.Is<IReadOnlyList<string>>(a => a.Contains("clone-target")), "");
    }

    [Fact]
    public void Idea_with_no_argument_asks_in_the_session()
    {
        Run(new IdeaCommand(_paths, _files, _processes, Host()));

        Assert.Equal("/factory:idea", Attached.Single().Args[^1]);
        _processes.DidNotReceiveWithAnyArgs().Run(default!, default!, default!);
    }

    private void Cli(string command, string[] args, string stdout) =>
        _processes.Run("dotnet", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { _paths.CliDll, command }.Concat(args))), "")
            .Returns(new ProcessResult(0, stdout, ""));
}
