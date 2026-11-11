using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>Start-Destroy's detached <c>factory.ps1</c>, against a stand-in factory.ps1 that records its arguments.</summary>
public sealed class FactoryLauncherTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Starts_factory_ps1_with_the_arguments_and_returns_without_waiting_for_it()
    {
        var scripts = Directory.CreateDirectory(_dir["scripts"]).FullName;
        var marker = _dir["ran.txt"];
        File.WriteAllText(Path.Combine(scripts, "factory.ps1"), $"Start-Sleep -Seconds 2; Set-Content -LiteralPath '{marker}' ($args -join ' ')");
        var paths = new FactoryPaths(Options.Create(new FactoryOptions { Scripts = scripts }), new GitClient(new ProcessRunner()));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        new FactoryLauncher(paths).StartDetached(["destroy", "J-001"]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "StartDetached waited for the child");
        Assert.False(File.Exists(marker));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(marker) && DateTime.UtcNow < deadline) await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal("destroy J-001", File.ReadAllText(marker).Trim());
    }

    [Fact]
    public async Task The_child_holds_none_of_its_parents_output_so_a_caller_reading_it_is_not_kept_waiting()
    {
        // The runner reads `merge`'s output to its end. A detached destroy that inherited that pipe would hold it open.
        var scripts = Directory.CreateDirectory(_dir["scripts"]).FullName;
        var marker = _dir["ran.txt"];
        File.WriteAllText(Path.Combine(scripts, "factory.ps1"), $"Start-Sleep -Seconds 8; Set-Content -LiteralPath '{marker}' done");
        var bin = AppContext.BaseDirectory;
        var parent = string.Join("; ",
            $"foreach ($d in 'Microsoft.Extensions.Options', 'Factory.Core', 'Factory.Store') {{ Add-Type -Path (Join-Path '{bin}' \"$d.dll\") }}",
            $"$o = [Microsoft.Extensions.Options.Options]::Create([Factory.Store.FactoryOptions]@{{ Scripts = '{scripts}' }})",
            "$paths = [Factory.Store.FactoryPaths]::new($o, [Factory.Store.GitClient]::new([Factory.Store.ProcessRunner]::new()))",
            "[Factory.Store.FactoryLauncher]::new($paths).StartDetached([string[]]@('destroy', 'J-001'))",
            "'started'");
        var psi = new System.Diagnostics.ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-Command", parent }) psi.ArgumentList.Add(a);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = await p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("started", ""), (output.Trim(), (await stderr).Trim()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(7), $"reading the parent's output took {watch.Elapsed}: the child held it");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(marker) && DateTime.UtcNow < deadline) await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(marker), "the detached child did not run to its end after its parent exited");
    }
}
