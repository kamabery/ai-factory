using System.Diagnostics;
using System.Text;
using Factory.Core;
using Factory.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// A gate command (contract, "Gate"): <c>pwsh -NoProfile -Command</c>, or the shell FACTORY_GATE_SHELL names, with
/// Quote-NativeArgs applied, in the work dir. Output is read as UTF-8 (npm and vite write it whatever the console's code
/// page), stdout and stderr in the order they arrive. A shell that cannot start fails the command with exit 127.
/// </summary>
public sealed class GateShell(IOptions<FactoryOptions> options) : IGateShell
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public ShellResult Run(string command, string workingDirectory)
    {
        var shell = string.IsNullOrEmpty(options.Value.GateShell) ? "pwsh" : options.Value.GateShell;
        var psi = new ProcessStartInfo(shell)
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
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(NativeArgs.Quote(command));
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
            catch (System.ComponentModel.Win32Exception e) { return new ShellResult(127, $"cannot start the gate shell '{shell}': {e.Message}{Environment.NewLine}"); }
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
