using System.Diagnostics;
using System.Text;
using Factory.Core;
using Factory.Core.Abstractions;

namespace Factory.Store;

/// <summary>
/// factory.ps1's Invoke-GateCommand: <c>pwsh -NoProfile -Command</c> with Quote-NativeArgs applied, in the work dir. Output
/// is read as UTF-8 (npm and vite write it whatever the console's code page), stdout and stderr in the order they arrive.
/// </summary>
public sealed class GateShell : IGateShell
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public ShellResult Run(string command, string workingDirectory)
    {
        var psi = new ProcessStartInfo("pwsh")
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
            p.Start();
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
