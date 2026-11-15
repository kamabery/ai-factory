using System.Diagnostics;
using System.Text;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// A gate command (contract, "Gate"), in the work dir: a program and its arguments with no shell, <c>factory</c> being this
/// CLI (<c>dotnet &lt;factory.dll&gt;</c>). A shell line left from before that rule runs only through FACTORY_GATE_SHELL
/// (<c>&lt;shell&gt; -NoProfile -Command</c>, Quote-NativeArgs applied). Output is read as UTF-8 (npm and vite write it
/// whatever the console's code page), stdout and stderr in the order they arrive. What cannot start fails with exit 127.
/// </summary>
public sealed class GateShell(IOptions<FactoryOptions> options, IFactoryPaths paths) : IGateShell
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public ShellResult Run(GateCommand gate, string workingDirectory)
    {
        var nl = Environment.NewLine;
        if (GateLine.Problem(gate.Cmd) is not null)
        {
            if (string.IsNullOrEmpty(options.Value.GateShell))
                return new ShellResult(127, $"gate line '{gate.Name}' needs a shell: set FACTORY_GATE_SHELL to the PowerShell 7 it was written for to finish this job, or discard it and approve a rewritten spec{nl}");
            var shell = options.Value.GateShell;
            return Start(shell, ["-NoProfile", "-Command", NativeArgs.Quote(gate.Cmd)], workingDirectory, $"cannot start the gate shell '{shell}'");
        }
        var args = GateLine.Split(gate.Cmd);
        var (program, rest) = args[0].Equals("factory", StringComparison.OrdinalIgnoreCase)
            ? ("dotnet", (IReadOnlyList<string>)[paths.CliDll, .. args.Skip(1)])
            : (ProgramPath.Resolve(args[0], Environment.GetEnvironmentVariable("PATH") ?? "", Environment.GetEnvironmentVariable("PATHEXT") ?? "",
                OperatingSystem.IsWindows(), File.Exists), (IReadOnlyList<string>)[.. args.Skip(1)]);
        return Start(program, rest, workingDirectory, $"cannot start '{args[0]}'");
    }

    private static ShellResult Start(string program, IReadOnlyList<string> args, string workingDirectory, string cannot)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);
        var output = new StringBuilder();
        void Line(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output) output.Append(e.Data).Append(Environment.NewLine);
        }
        // A child with a console of its own writes in its code page: give it UTF-8, as factory.ps1 did, then put it back.
        var restore = SetConsoleUtf8();
        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += Line;
            p.ErrorDataReceived += Line;
            try { p.Start(); }
            catch (System.ComponentModel.Win32Exception e) { return new ShellResult(127, $"{cannot}: {e.Message}{Environment.NewLine}"); }
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            lock (output) return new ShellResult(p.ExitCode, output.ToString());
        }
        finally { restore?.Invoke(); }
    }

    private static Action? SetConsoleUtf8()
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
