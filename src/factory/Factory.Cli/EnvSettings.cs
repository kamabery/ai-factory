using System.Text;
using System.Text.RegularExpressions;
using Factory.Core.Abstractions;

namespace Factory.Cli;

/// <summary>
/// init's edits to .env (contract, "Secrets, init and doctor"): one setting set in place or appended, a line commented out,
/// and a new .env started from .env.example. Every other line is kept as it was.
/// </summary>
public sealed partial class EnvSettings(IFileStore files)
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Creates <paramref name="path"/> from <paramref name="example"/> (or empty) when it does not exist.</summary>
    public void EnsureFrom(string path, string example)
    {
        if (files.ReadText(path) is not null) return;
        files.WriteAtomic(path, Utf8.GetBytes(files.ReadText(example) ?? ""));
    }

    /// <summary>NAME=value: the first live or commented-out line for NAME is replaced, else the line is appended.</summary>
    public void Set(string path, string name, string value)
    {
        var lines = Lines(path);
        var i = lines.FindIndex(l => Named(l, name, live: true));
        if (i < 0) i = lines.FindIndex(l => Named(l, name, live: false));
        if (i >= 0) lines[i] = $"{name}={value}";
        else lines.Add($"{name}={value}");
        Write(path, lines);
    }

    /// <summary>Comments out each live NAME= line, with a note why, and returns the values it held.</summary>
    public string? CommentOut(string path, string name, string note)
    {
        var lines = Lines(path);
        string? value = null;
        for (var n = 0; n < lines.Count; n++)
        {
            if (!Named(lines[n], name, live: true)) continue;
            value ??= Store.EnvFile.Parse(lines[n]).GetValueOrDefault(name);
            lines[n] = $"# {name} {note}";
        }
        Write(path, lines);
        return value;
    }

    private List<string> Lines(string path) => [.. (files.ReadText(path) ?? "").ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Where((l, i) => i > 0 || l.Length > 0)];

    private void Write(string path, List<string> lines) => files.WriteAtomic(path, Utf8.GetBytes(string.Join(Environment.NewLine, lines) + Environment.NewLine));

    private static bool Named(string line, string name, bool live) =>
        (live ? LiveLine() : CommentedLine()).Match(line) is { Success: true } m && string.Equals(m.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=")] private static partial Regex LiveLine();
    [GeneratedRegex(@"^\s*#\s*([A-Za-z_][A-Za-z0-9_]*)\s*=")] private static partial Regex CommentedLine();
}
