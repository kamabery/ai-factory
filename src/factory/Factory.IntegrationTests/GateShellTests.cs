using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>
/// Gate lines run as a program and its arguments with no shell (contract, "Gate"), on every OS; a shell line left from
/// before that rule runs only through the shell FACTORY_GATE_SHELL names.
/// </summary>
public sealed class GateShellTests : IDisposable
{
    private static readonly string CliDll = GitHubStub.RepoFile("artifacts", "bin", "Factory.Cli", "debug", "factory.dll");
    private static readonly string TestHost = GitHubStub.RepoFile("artifacts", "bin", "Factory.TestHost", "debug",
        OperatingSystem.IsWindows() ? "Factory.TestHost.exe" : "Factory.TestHost");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private GateShell Shell(string? shell = null) =>
        new(Options.Create(new FactoryOptions { GateShell = shell }), new FactoryPaths(
            Options.Create(new FactoryOptions { Home = _dir["state"], Scripts = _dir["scripts"], Templates = _dir["tp"], Cli = CliDll }),
            new GitClient(new ProcessRunner())));

    private ShellResult Run(string line, string? shell = null) => Shell(shell).Run(new GateCommand("g", line), _dir.Path);

    [Fact]
    public void Runs_a_program_from_PATH_in_the_work_dir_with_no_shell_keeping_UTF8_whole()
    {
        // npm and vite write UTF-8 bytes whatever the console's code page; git prints the value as the file has it.
        File.WriteAllText(Path.Combine(_dir.Path, "my gate.cfg"), "[a]\n\tb = built │ ok ✓\n");

        var r = Run("git config --file \"my gate.cfg\" --get a.b");

        Assert.Equal((0, "built │ ok ✓" + Environment.NewLine), (r.Code, r.Out));
    }

    [Fact]
    public void A_program_of_factory_runs_this_CLI_and_both_its_streams_are_kept()
    {
        var failed = Run("factory check file -var-file=envs/ephemeral.tfvars");
        var refused = Run("factory check");

        Assert.Equal((1, "check failed: -var-file=envs/ephemeral.tfvars does not exist" + Environment.NewLine), (failed.Code, failed.Out));
        Assert.Equal(2, refused.Code);
        Assert.StartsWith("check refused: usage: factory check file <path>", refused.Out);
    }

    [Fact]
    public void A_program_that_cannot_start_fails_the_command_with_127()
    {
        var r = Run("no-such-gate-program --x");

        Assert.Equal(127, r.Code);
        Assert.StartsWith("cannot start 'no-such-gate-program': ", r.Out);
    }

    [Fact]
    public void On_Windows_a_cmd_file_runs_without_its_extension()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PATHEXT is Windows'");
        File.WriteAllText(Path.Combine(_dir.Path, "gate.cmd"), "@echo from cmd %1\r\n@exit /b 4\r\n");

        var r = Run(Path.Combine(_dir.Path, "gate") + " arg");

        Assert.Equal((4, "from cmd arg"), (r.Code, r.Out.Trim()));
    }

    [Fact]
    public void A_shell_line_without_FACTORY_GATE_SHELL_fails_with_127_saying_how_to_finish_the_job()
    {
        var r = Shell().Run(new GateCommand("ui", "Set-Location src/ui; npm run build"), _dir.Path);

        Assert.Equal((127, "gate line 'ui' needs a shell: set FACTORY_GATE_SHELL to the PowerShell 7 it was written for to finish this job, or discard it and approve a rewritten spec" + Environment.NewLine), (r.Code, r.Out));
    }

    [Fact]
    public void A_shell_line_runs_through_FACTORY_GATE_SHELL_with_a_dotted_name_value_argument_quoted()
    {
        // J-022: PowerShell handed terraform `-var-file=envs/ephemeral` and `.tfvars` as two arguments. The stand-in shell echoes.
        var r = Run("Set-Location IaC; terraform plan -var-file=envs/ephemeral.tfvars", TestHost);

        Assert.Equal((0, "shell: -NoProfile|-Command|Set-Location IaC; terraform plan \"-var-file=envs/ephemeral.tfvars\""), (r.Code, r.Out.Trim()));
    }

    [Fact]
    public void A_gate_shell_that_cannot_start_fails_the_shell_line_with_127()
    {
        var r = Run("Write-Host never", "no-such-gate-shell");

        Assert.Equal(127, r.Code);
        Assert.StartsWith("cannot start the gate shell 'no-such-gate-shell': ", r.Out);
    }

    [Fact]
    public void A_line_that_needs_no_shell_ignores_FACTORY_GATE_SHELL()
    {
        Assert.Equal(0, Run("git --version", "no-such-gate-shell").Code);
    }
}
