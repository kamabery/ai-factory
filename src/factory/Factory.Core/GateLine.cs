using System.Text;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>
/// A gate line is a program and its arguments, run with no shell (contract, "Gate"): split at spaces, a <c>"..."</c> or
/// <c>'...'</c> kept whole with its quotes dropped, no escapes (a Windows path's <c>\</c> stays). A shell line is one this
/// cannot run: a shell character outside quotes, an unterminated quote, or a cmdlet or builtin for its program.
/// </summary>
public static partial class GateLine
{
    private const string Check = "use factory check file";
    private const string Dir = "use the tool's directory flag or path";
    private const string Split1 = "use one gate line per command, or a script the target commits";
    private const string ShellChars = ";|&><$`(){}";

    private static readonly string[] Builtins = ["cd", "exit", "echo", "set", "export", "if", "test", "true", "false"];
    private static readonly string[] ReadsFiles = ["Test-Path", "Get-Content", "Select-String", "Get-Item", "Get-ChildItem"];
    private static readonly string[] ChangesDir = ["cd", "Set-Location", "Push-Location"];

    /// <summary>Why the line needs a shell, and how to rewrite it; null when it is a program and its arguments.</summary>
    public static GateLineProblem? Problem(string line)
    {
        var (args, shellChar, unterminated) = Scan(line);
        if (args.Count > 0)
        {
            var program = args[0];
            var hint = ReadsFiles.Contains(program, StringComparer.OrdinalIgnoreCase) ? Check
                : ChangesDir.Contains(program, StringComparer.OrdinalIgnoreCase) ? Dir : Split1;
            if (Cmdlet().IsMatch(program)) return new GateLineProblem($"`{program}` is a PowerShell cmdlet", hint);
            if (Builtins.Contains(program, StringComparer.OrdinalIgnoreCase)) return new GateLineProblem($"`{program}` is a shell builtin", hint);
        }
        if (shellChar is { } c) return new GateLineProblem($"`{c}` needs a shell", Split1);
        if (unterminated) return new GateLineProblem("an unterminated quote", Split1);
        return args.Count == 0 ? new GateLineProblem("no program", Split1) : null;
    }

    /// <summary>The program and its arguments of a line with no <see cref="Problem"/>.</summary>
    public static IReadOnlyList<string> Split(string line) => Scan(line).Args;

    private static (List<string> Args, char? ShellChar, bool Unterminated) Scan(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inArg = false;
        char? quote = null;
        char? shellChar = null;
        foreach (var c in line)
        {
            if (quote is { } q)
            {
                if (c == q) quote = null;
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; inArg = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (inArg) { args.Add(current.ToString()); current.Clear(); inArg = false; }
                continue;
            }
            if (shellChar is null && ShellChars.Contains(c)) shellChar = c;
            current.Append(c);
            inArg = true;
        }
        if (inArg) args.Add(current.ToString());
        return (args, shellChar, quote is not null);
    }

    [GeneratedRegex("^[A-Z][a-z]+-[A-Z][A-Za-z]*$")] private static partial Regex Cmdlet();
}
