using System.Diagnostics;
using System.Text;
using Factory.TestGates;

// dotnet run --project src/factory/Factory.TestGates [-- --tier unit|integration|all] [--no-build]: the .NET tests by tier,
// their counts, and with --tier all (the default) the coverage gates. Run before every PR (docs/TESTING.md).
var tier = "all";
var build = true;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--tier" && i + 1 < args.Length && args[i + 1] is "unit" or "integration" or "all") tier = args[++i];
    else if (args[i] == "--no-build") build = false;
    else
    {
        Console.Error.WriteLine("usage: Factory.TestGates [--tier unit|integration|all] [--no-build]");
        return 2;
    }
}

var root = FindSource(AppContext.BaseDirectory);
var output = Path.GetFullPath(Path.Combine(root, "..", "..", "artifacts", "test-results"));
if (Directory.Exists(output)) Directory.Delete(output, true);

if (build && Run("dotnet", ["build", Path.Combine(root, "Factory.slnx"), "-v", "q", "-nologo"]).Code is var b and not 0) return b;

var projects = new Dictionary<string, string> { ["unit"] = "Factory.UnitTests", ["integration"] = "Factory.IntegrationTests" };
var failed = false;
var counts = new List<TierCount>();
foreach (var t in tier == "all" ? projects.Keys.ToArray() : [tier])
{
    var dir = Path.Combine(output, t);
    var r = Run("dotnet", ["test", "--project", Path.Combine(root, projects[t], $"{projects[t]}.csproj"), "--no-build",
        "--coverage", "--coverage-output-format", "cobertura", "--results-directory", dir]);
    counts.Add(CoverageGates.Count(t, r.Log));
    failed |= r.Code != 0;
}

var files = Directory.Exists(output) ? Directory.EnumerateFiles(output, "*.cobertura.xml", SearchOption.AllDirectories).Select(File.ReadAllText) : [];
var gates = CoverageGates.Gate(CoverageGates.LineRates(files));
var gated = tier == "all";
failed |= gated && gates.Any(g => !g.Ok);
var summary = CoverageGates.Summary(counts, gates, gated);
foreach (var line in summary) Console.WriteLine(line);
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } step) File.AppendAllLines(step, summary);
return failed ? 1 : 0;

// src/factory, above this build's folder (artifacts/bin/Factory.TestGates/<config>/).
static string FindSource(string from)
{
    for (var d = new DirectoryInfo(from); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "src", "factory", "Factory.slnx"))) return Path.Combine(d.FullName, "src", "factory");
    throw new InvalidOperationException($"no src/factory/Factory.slnx above {from}");
}

// A child whose output is shown as it comes and kept for its counts.
static (int Code, string Log) Run(string program, IReadOnlyList<string> args)
{
    var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in args) psi.ArgumentList.Add(a);
    var log = new StringBuilder();
    void Line(object _, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        lock (log) log.AppendLine(e.Data);
        Console.WriteLine(e.Data);
    }
    using var p = new Process { StartInfo = psi };
    p.OutputDataReceived += Line;
    p.ErrorDataReceived += Line;
    p.Start();
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    p.WaitForExit();
    lock (log) return (p.ExitCode, log.ToString());
}
