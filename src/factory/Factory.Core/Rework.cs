using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary><c>rework</c>'s note: its <c>Scope +:</c> lines widen the spec's Scope: In (contract, "Targets").</summary>
public static partial class Rework
{
    /// <summary>The globs of every <c>Scope +: a, b</c> line, trimmed of spaces and backticks.</summary>
    public static IReadOnlyList<string> Widening(string note) =>
        [.. note.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => ScopePlus().Match(l)).Where(m => m.Success)
            .SelectMany(m => m.Groups[1].Value.Split(','))
            .Select(g => g.Trim().Trim('`').Trim()).Where(g => g.Length > 0)];

    /// <summary>The spec's lines with each glob added under Scope: In, after line <paramref name="at"/>, marked with the round.</summary>
    public static IReadOnlyList<string> Widen(IReadOnlyList<string> lines, int at, IReadOnlyList<string> globs, int round)
    {
        var l = lines.ToList();
        l.InsertRange(at + 1, globs.Select(g => $"  - `{g}` (rework {round})"));
        return l;
    }

    [GeneratedRegex(@"^\s*Scope \+:\s*(.+)$", RegexOptions.IgnoreCase)] private static partial Regex ScopePlus();
}
