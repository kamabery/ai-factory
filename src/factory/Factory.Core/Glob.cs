using System.Text;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>Scope globs (contract, "Targets"): <c>*</c> matches within one path segment, <c>**</c> across segments.</summary>
public static class Glob
{
    public static string ToRegex(string glob)
    {
        var r = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; r.Append("(?:.*/)?"); }
                else r.Append(".*");
            }
            else if (c == '*') r.Append("[^/]*");
            else if (c == '?') r.Append("[^/]");
            else r.Append(Regex.Escape(c.ToString()));
        }
        return r.Append('$').ToString();
    }

    /// <summary>Case-insensitive, as PowerShell's <c>-match</c>.</summary>
    public static bool Matches(string glob, string path) =>
        Regex.IsMatch(path, ToRegex(glob), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The paths no glob matches, in their order: build check 1's strays.</summary>
    public static IReadOnlyList<string> Strays(IReadOnlyList<string> globs, IEnumerable<string> paths) =>
        [.. paths.Where(p => p.Length > 0 && !globs.Any(g => Matches(g, p)))];
}
