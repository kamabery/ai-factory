using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>
/// <c>factory check file</c>'s rules (contract, "factory commands"): a gate's check that a file exists and has the headings,
/// text and lines it asks for, all without case. Shell-neutral, so a spec's gate reads the same on every OS.
/// </summary>
public static partial class FileCheck
{
    /// <summary>One <c>check failed:</c> line per rule that does not hold, in the order given; a missing file (null) fails once.</summary>
    public static IReadOnlyList<string> Failures(FileCheckRequest request, string? text)
    {
        var p = request.Path;
        if (text is null) return [$"check failed: {p} does not exist"];
        var lines = LineBreak().Split(text);
        var headings = lines.Select(l => Heading().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value.Trim()).ToList();
        var failures = new List<string>();
        foreach (var r in request.Rules)
        {
            var failed = r.Option switch
            {
                "--heading" => !headings.Any(h => Matches(h, r.Value)) ? $"has no heading matching '{r.Value}'" : null,
                "--contains" => !text.Contains(r.Value, StringComparison.OrdinalIgnoreCase) ? $"does not contain '{r.Value}'" : null,
                _ => !lines.Any(l => Matches(l, r.Value)) ? $"has no line matching '{r.Value}'" : null,
            };
            if (failed is not null) failures.Add($"check failed: {p} {failed}");
        }
        return failures;
    }

    private static bool Matches(string input, string pattern) =>
        Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    [GeneratedRegex(@"^ {0,3}#{1,6} +(.*?)(?: +#+)? *$")] private static partial Regex Heading();
    [GeneratedRegex("\r?\n")] private static partial Regex LineBreak();
}
