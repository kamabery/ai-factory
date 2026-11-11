using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary>Gate commands through a real <c>pwsh -NoProfile -Command</c>, as factory.ps1's Invoke-GateCommand ran them.</summary>
public sealed class GateShellTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Runs_in_the_work_dir_and_returns_the_exit_code_with_stdout_and_stderr()
    {
        var r = new GateShell().Run("(Get-Location).Path; [Console]::Error.WriteLine('oops'); exit 3", _dir.Path);

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

        var r = new GateShell().Run(emit, _dir.Path);

        Assert.Equal(0, r.Code);
        Assert.Equal("built │ ok ✓" + Environment.NewLine, r.Out);
    }

    [Fact]
    public void Passes_a_native_name_value_argument_with_a_dot_whole()
    {
        // J-022: pwsh handed terraform `-var-file=envs/ephemeral` and `.tfvars` as two arguments.
        var echo = OperatingSystem.IsWindows() ? "cmd /c echo" : "/bin/echo";

        var r = new GateShell().Run($"{echo} -var-file=envs/ephemeral.tfvars", _dir.Path);

        Assert.Equal("-var-file=envs/ephemeral.tfvars", r.Out.Trim());
    }
}
