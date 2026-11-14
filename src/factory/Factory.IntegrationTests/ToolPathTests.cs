using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary>
/// A batch-file launcher such as az.cmd finds its own folder through %~dp0, which is only right when the process is
/// started by the script's full path: started as plain "az.cmd", az prints "Failed to load python executable."
/// </summary>
public sealed class ToolPathTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_cmd_shim_runs_only_by_the_full_path_found_on_PATH()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "batch-file shims are Windows only");
        var bin = Directory.CreateDirectory(_dir["wbin"]).FullName;
        File.WriteAllText(Path.Combine(_dir.Path, "beside.txt"), "found it");
        File.WriteAllText(Path.Combine(bin, "fake.cmd"), """@IF EXIST "%~dp0\..\beside.txt" (type "%~dp0\..\beside.txt") ELSE (echo Failed to load python executable. & exit /b 1)""");
        var path = string.Join(Path.PathSeparator, _dir["nothing-here"], bin);

        var full = ToolPath.Find("fake.cmd", path);

        Assert.Equal(Path.Combine(bin, "fake.cmd"), full);
        Assert.Equal((0, "found it"), Run(full));
        Assert.Equal("fake.cmd", ToolPath.Find("fake.cmd", _dir["nothing-here"]));
    }

    [Fact]
    public void Az_is_started_by_its_full_path_on_Windows_and_by_name_elsewhere()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Equal("az", StateToken.AzProgram); return; }
        Assert.SkipWhen(ToolPath.Find("az.cmd", Environment.GetEnvironmentVariable("PATH")) == "az.cmd", "az is not installed here");
        Assert.True(Path.IsPathRooted(StateToken.AzProgram));
    }

    private (int, string) Run(string file)
    {
        var r = new ProcessRunner().Run(file, [], _dir.Path);
        return (r.ExitCode, r.StdOut.Trim());
    }
}
