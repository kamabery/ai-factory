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
