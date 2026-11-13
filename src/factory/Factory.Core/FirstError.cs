using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>
/// deploy.ps1's First-Error: the first line of a deploy log that reads like an error, for the event and the flag.
/// GitHub prints each step's script in cyan (ESC[36;1m) before running it, so an <c>echo "::error::..."</c> there is
/// source, not an error. An action's <c>##[error]</c> or Terraform's <c>Error:</c> is the real thing; the closing
/// "Process completed with exit code" says only that something failed, so the cause is in the output of the step it
/// closes. A Terraform warning is not an error, whatever words it uses (canary 21 quoted one instead of its 42501),
/// and a Terraform line can need the rest of its box. Every match ignores case, as PowerShell's <c>-match</c> does.
/// </summary>
public static partial class FirstError
{
    public static string Of(string? log)
    {
        var boxed = new List<string>();
        var inWarning = false;
        foreach (var raw in (log ?? "").Split('\n'))
        {
            if (Echoed().IsMatch(raw)) continue;
            var l = Stamp().Replace(Colour().Replace(raw, ""), "", 1).TrimEnd();
            // HCP's structured log: only its errors count, and they read as Terraform's own do.
            if (l.StartsWith("{\"@level\"", StringComparison.OrdinalIgnoreCase))
            {
                if (!l.StartsWith("{\"@level\":\"error\"", StringComparison.OrdinalIgnoreCase)) continue;
                try { l = PsJsonReader.Text((JsonNode.Parse(l) as JsonObject)?["@message"]); }
                catch (JsonException) { continue; }
            }
            if (WarningStart().IsMatch(l)) inWarning = true;
            if (!inWarning) boxed.Add(l);
            if (BoxEnd().IsMatch(l)) inWarning = false;
        }
        var lines = boxed.Select(b => BoxMarks().Replace(b, "").Trim()).ToList();
        var at = lines.FindIndex(l => (ActionError().IsMatch(l) && !Generic().IsMatch(l)) || TerraformError().IsMatch(l));
        if (at >= 0)
        {
            if (BoxLine().IsMatch(boxed[at])) return Terraform(boxed, at);
            return ActionError().Replace(lines[at], "", 1);
        }
        var exit = lines.FindIndex(l => Generic().IsMatch(l));
        // factory.ps1 tests the index for truth: an exit line that is the very first line is not used.
        if (exit > 0)
        {
            var group = Enumerable.Range(0, exit).LastOrDefault(i => Group().IsMatch(lines[i]), -1);
            var step = PsRange(group >= 0 ? group + 1 : 0, exit - 1).Select(i => lines[i]).ToList();
            var line = step.FirstOrDefault(s => Cause().IsMatch(s));
            if (string.IsNullOrEmpty(line) && group >= 0) line = step.LastOrDefault(s => s.Length > 0);
            if (!string.IsNullOrEmpty(line)) return line;
        }
        var any = lines.FirstOrDefault(l => Word().IsMatch(l) && !Generic().IsMatch(l));
        if (string.IsNullOrEmpty(any)) any = lines.LastOrDefault(l => l.Length > 0);
        return ActionError().Replace(any ?? "", "", 1);
    }

    /// <summary>
    /// A Terraform error box as one line: the summary, which azurerm wraps, and the response when the provider left it
    /// further down the box (canary 24's summary was only "Failed to execute action").
    /// </summary>
    private static string Terraform(List<string> boxed, int at)
    {
        var box = boxed.Skip(at).TakeWhile(b => BoxLine().IsMatch(b)).Select(b => BoxLine().Replace(b, "", 1).Trim()).ToList();
        var blank = box.FindIndex(b => b.Length == 0);
        // factory.ps1 tests the index for truth; the box's first line is its Error: summary, so never blank.
        var summary = string.Join(' ', box.Take(blank > 0 ? blank : box.Count).Where(b => b.Length > 0));
        var detail = box.Where(b => Detail().IsMatch(b)).ToList();
        // A provider-install failure's cause is a paragraph that Terraform wraps (J-029's registry timeout).
        var install = box.FindIndex(b => Installing().IsMatch(b));
        if (install >= 0)
        {
            var end = box.FindIndex(install + 1, b => b.Length == 0);
            detail.Insert(0, string.Join(' ', box.Skip(install).Take((end > 0 ? end : box.Count) - install)));
        }
        return detail.Count > 0 ? $"{summary.TrimEnd(':')}: {string.Join("; ", detail)}" : summary;
    }

    /// <summary>PowerShell's <c>a..b</c>: ascending, or descending when <paramref name="from"/> is past <paramref name="to"/>.</summary>
    private static IEnumerable<int> PsRange(int from, int to) =>
        from <= to ? Enumerable.Range(from, to - from + 1) : Enumerable.Range(to, from - to + 1).Reverse();

    private const RegexOptions Ps = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\x1b\[(36;1|33)m", Ps)] private static partial Regex Echoed();
    [GeneratedRegex(@"\x1b\[[0-9;]*m", Ps)] private static partial Regex Colour();
    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT[\d:.]+Z\s?", Ps)] private static partial Regex Stamp();
    [GeneratedRegex(@"^│\s*Warning: ", Ps)] private static partial Regex WarningStart();
    [GeneratedRegex(@"^\s*╵", Ps)] private static partial Regex BoxEnd();
    [GeneratedRegex(@"^[│╷╵\s]+", Ps)] private static partial Regex BoxMarks();
    [GeneratedRegex(@"^\s*│", Ps)] private static partial Regex BoxLine();
    [GeneratedRegex(@"^##\[error\]", Ps)] private static partial Regex ActionError();
    [GeneratedRegex(@"^##\[error\]Process completed with exit code", Ps)] private static partial Regex Generic();
    [GeneratedRegex(@"^Error: ", Ps)] private static partial Regex TerraformError();
    [GeneratedRegex(@"^##\[(end)?group\]", Ps)] private static partial Regex Group();
    [GeneratedRegex(@"exception|\berror\b|\bfatal\b|\bdenied\b", Ps)] private static partial Regex Cause();
    [GeneratedRegex(@"\berror\b", Ps)] private static partial Regex Word();
    [GeneratedRegex(@"^(RESPONSE \d|ERROR CODE: |Message: )", Ps)] private static partial Regex Detail();
    [GeneratedRegex(@"^Error while installing ", Ps)] private static partial Regex Installing();
}
