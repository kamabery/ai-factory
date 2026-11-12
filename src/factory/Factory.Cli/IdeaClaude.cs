using System.Diagnostics;
using System.Text;
using Factory.Cli.Abstractions;

namespace Factory.Cli;

/// <summary>Starts claude (or its test stand-in) with the prompt on stdin, and reads its stream-json line by line.</summary>
public sealed class IdeaClaude : IIdeaClaude
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public (int Code, string Err) Run(string file, IReadOnlyList<string> args, string workDir, IReadOnlyDictionary<string, string?> env, string stdin, Action<string> line)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (name, value) in env)
            if (value is null) psi.Environment.Remove(name); else psi.Environment[name] = value;
        var err = new StringBuilder();
        var gate = new object();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) line(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) { err.AppendLine(e.Data); if (err.Length > 4000) err.Remove(0, err.Length - 4000); } };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        p.WaitForExit();
        lock (gate) return (p.ExitCode, err.ToString().Trim());
    }
}
