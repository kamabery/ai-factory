using System.Text.RegularExpressions;

namespace Factory.Store;

/// <summary>env-file.ps1's Read-EnvFile: NAME=value lines, quotes stripped, comments skipped, names without case.</summary>
public static partial class EnvFile
{
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text is null) return vars;
        foreach (var line in text.Split('\n'))
        {
            if (Comment().IsMatch(line) || Line().Match(line.TrimEnd('\r')) is not { Success: true } m) continue;
            var v = m.Groups[2].Value;
            // As Read-EnvFile's two -replace calls: double quotes, then single.
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
            if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') v = v[1..^1];
            vars[m.Groups[1].Value] = v;
        }
        return vars;
    }

    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$")] private static partial Regex Line();
    [GeneratedRegex(@"^\s*#")] private static partial Regex Comment();
}
