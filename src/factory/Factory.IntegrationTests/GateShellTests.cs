using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>Gate commands through a real <c>pwsh -NoProfile -Command</c>, or the shell FACTORY_GATE_SHELL names.</summary>
public sealed class GateShellTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static GateShell Shell(string? shell = null) => new(Options.Create(new FactoryOptions { GateShell = shell }));

    [Fact]
    public void FACTORY_GATE_SHELL_names_the_shell_by_path_so_pwsh_need_not_be_on_PATH()
    {
        var pwsh = Shell().Run("(Get-Process -Id $PID).Path", _dir.Path).Out.Trim();

        var r = Shell(pwsh).Run("'by path'", _dir.Path);

        Assert.Equal((0, "by path"), (r.Code, r.Out.Trim()));
    }

    [Fact]
    public void A_gate_shell_that_cannot_start_fails_the_command_with_127()
    {
        var r = Shell("no-such-gate-shell").Run("'never'", _dir.Path);

        Assert.Equal(127, r.Code);
        Assert.StartsWith("cannot start the gate shell 'no-such-gate-shell': ", r.Out);
    }

    [Fact]
    public void Runs_in_the_work_dir_and_returns_the_exit_code_with_stdout_and_stderr()
    {
        var r = Shell().Run("(Get-Location).Path; [Console]::Error.WriteLine('oops'); exit 3", _dir.Path);

        Assert.Equal(3, r.Code);
        Assert.Contains(_dir.Path, r.Out);
        Assert.Contains("oops", r.Out);
        Assert.EndsWith(Environment.NewLine, r.Out);
    }

    [Fact]
    public void Keeps_raw_UTF8_output_intact()
    {
        // npm and vite write UTF-8 bytes whatever the console's code page is.
        const string emit = "$b = [Text.Encoding]::UTF8.GetBytes('built ' + [char]0x2502 + ' ok ' + [char]0x2713); $s = [Console]::OpenStandardOutput(); $s.Write($b, 0, $b.Length); $s.Flush()";

        var r = Shell().Run(emit, _dir.Path);

        Assert.Equal(0, r.Code);
        Assert.Equal("built │ ok ✓" + Environment.NewLine, r.Out);
    }

    [Fact]
    public void Passes_a_native_name_value_argument_with_a_dot_whole()
    {
        // J-022: pwsh handed terraform `-var-file=envs/ephemeral` and `.tfvars` as two arguments.
        var echo = OperatingSystem.IsWindows() ? "cmd /c echo" : "/bin/echo";

        var r = Shell().Run($"{echo} -var-file=envs/ephemeral.tfvars", _dir.Path);

        Assert.Equal("-var-file=envs/ephemeral.tfvars", r.Out.Trim());
    }
}
