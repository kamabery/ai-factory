using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Runner.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Runner;

/// <summary>
/// One claude pass (contract, "Runner"): writes headless-settings.json, then runs
/// <c>claude -p &lt;prompt&gt; [--resume s] --plugin-dir ... --settings ... --add-dir &lt;state&gt; --output-format stream-json
/// --verbose --permission-mode acceptEdits</c> in the work dir, or FACTORY_CLAUDE_CMD through <c>pwsh -Command</c>.
/// Every output line goes to the run log as it arrives, through one stream shared for reading, so a reader (the
/// dashboard's Output tab) can never make a write fail.
/// </summary>
public sealed class ClaudeRunner(IFactoryPaths paths, IFileStore files, IOptions<RunnerOptions> options) : IClaudeRunner
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private string FactoryRoot => Path.GetFullPath(Path.Combine(paths.ScriptsDir, "..", ".."));

    public ClaudeRun Run(string jobId, string prompt, string workDir, string log, IReadOnlyDictionary<string, string?> childEnv, string resumeSession)
    {
        var settings = Path.Combine(paths.Home, "headless-settings.json");
        files.WriteAtomic(settings, Utf8.GetBytes(PsJson.Write(HeadlessSettings.Build(FactoryRoot, paths.TemplatesRoot, paths.Home), Environment.NewLine)));
        files.EnsureDirectory(Path.GetDirectoryName(log)!);

        var psi = Start(prompt, settings, resumeSession);
        psi.WorkingDirectory = workDir;
        psi.RedirectStandardOutput = psi.RedirectStandardError = psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Utf8;
        foreach (var (name, value) in childEnv)
            if (value is null) psi.Environment.Remove(name); else psi.Environment[name] = value;
        // The skill, the agents and the hooks call `factory` by name: this build's CLI comes first on PATH.
        psi.Environment["PATH"] = Path.GetDirectoryName(Path.GetFullPath(paths.CliDll)) + Path.PathSeparator + (psi.Environment.TryGetValue("PATH", out var path) ? path : "");
        if (options.Value.DotnetRoot is { } root && !(psi.Environment.TryGetValue("DOTNET_ROOT", out var had) && !string.IsNullOrEmpty(had))) psi.Environment["DOTNET_ROOT"] = root;
        psi.Environment["FACTORY_JOB"] = jobId;
        psi.Environment["FACTORY_HOME"] = paths.Home;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var output = new StreamWriter(new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), Utf8) { AutoFlush = true };
        JsonObject? result = null;
        var gate = new object();
        void Line(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (gate)
            {
                output.Write(e.Data + "\n");
                if (e.Data.Contains("\"total_cost_usd\"", StringComparison.Ordinal))
                {
                    try { result = JsonNode.Parse(e.Data) as JsonObject ?? result; } catch (JsonException) { }
                }
            }
        }
        // claude writes UTF-8; a runner started by serve.ps1 has a console on the OEM code page, which a child inherits.
        var restore = ConsoleUtf8();
        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += Line;
            p.ErrorDataReceived += Line;
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            lock (gate) return new ClaudeRun(p.ExitCode, result);
        }
        finally { restore?.Invoke(); }
    }

    private ProcessStartInfo Start(string prompt, string settings, string resumeSession)
    {
        if (options.Value.ClaudeCmd is { Length: > 0 } fake)
        {
            var psi = new ProcessStartInfo("pwsh");
            foreach (var a in new[] { "-NoProfile", "-Command", fake }) psi.ArgumentList.Add(a);
            // A fake claude sees the session it would continue.
            psi.Environment["FACTORY_CLAUDE_RESUME"] = resumeSession;
            return psi;
        }
        var claude = new ProcessStartInfo(Factory.Store.ClaudeCli.Executable());
        foreach (var a in Arguments(prompt, resumeSession, FactoryRoot, paths.TemplatesRoot, settings, paths.Home)) claude.ArgumentList.Add(a);
        return claude;
    }

    /// <summary>claude's arguments for one pass (contract, "Runner", step 3).</summary>
    public static IReadOnlyList<string> Arguments(string prompt, string resumeSession, string factoryRoot, string templatesRoot, string settings, string home)
    {
        var args = new List<string> { "-p", prompt };
        if (resumeSession.Length > 0) args.AddRange(["--resume", resumeSession]);
        // The factory's own plugin, and the template workspace as the aspire-starter plugin (boot-strap-app, enhance-app).
        args.AddRange(["--plugin-dir", Path.Combine(factoryRoot, "factory", "plugin"), "--plugin-dir", templatesRoot]);
        args.AddRange(["--settings", settings, "--add-dir", home, "--output-format", "stream-json", "--verbose", "--permission-mode", "acceptEdits"]);
        return args;
    }

    private static Action? ConsoleUtf8()
    {
        try
        {
            var was = Console.OutputEncoding;
            Console.OutputEncoding = Utf8;
            return () => { try { Console.OutputEncoding = was; } catch (IOException) { } };
        }
        catch (IOException) { return null; }
    }
}
