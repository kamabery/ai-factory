using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Factory.TestGates;

/// <summary>
/// The suite's gates (docs/TESTING.md): each tier's counts from its log, line coverage merged across the tiers' Cobertura
/// files (a line counted once, covered when any tier hit it), and the bars: 90% on Factory.Core, 70% on every other
/// assembly the tests reach, apart from the hosts with no logic, the test tooling, and generated code.
/// </summary>
public static partial class CoverageGates
{
    public const double DefaultGate = 0.70;

    private static readonly Dictionary<string, double> Gates = new() { ["Factory.Core"] = 0.90 };

    /// <summary>AppHost and ServiceDefaults have no logic; TestHost, Simulator and TestGates are the tests' own tooling.</summary>
    private static readonly string[] Excluded = ["Factory.AppHost", "Factory.ServiceDefaults", "Factory.TestHost", "Factory.Simulator", "Factory.TestGates"];

    /// <summary>The last <c>total:</c> and <c>succeeded:</c> lines of a tier's <c>dotnet test</c> log; none is 0.</summary>
    public static TierCount Count(string tier, string log) =>
        new(tier, Last(Total(), log), Last(Succeeded(), log));

    /// <summary>Each assembly's line rate across every Cobertura file.</summary>
    public static IReadOnlyDictionary<string, double> LineRates(IEnumerable<string> cobertura)
    {
        var lines = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var xml in cobertura)
            foreach (var pkg in XDocument.Parse(xml).Descendants("package"))
            {
                var name = (string?)pkg.Attribute("name") ?? "";
                if (Excluded.Contains(name) || name.EndsWith("Tests", StringComparison.Ordinal)) continue;
                if (!lines.TryGetValue(name, out var set)) lines[name] = set = [];
                foreach (var cls in pkg.Descendants("class"))
                {
                    var file = (string?)cls.Attribute("filename") ?? "";
                    // Source generators' output ([GeneratedRegex]'s RegexGenerator.g.cs) and dotnet ef's migrations are not code we write.
                    if (file.EndsWith(".g.cs", StringComparison.Ordinal) || Migrations().IsMatch(file)) continue;
                    foreach (var l in cls.Descendants("line"))
                    {
                        var key = $"{file}:{(string?)l.Attribute("number")}";
                        set[key] = set.GetValueOrDefault(key) || int.Parse((string?)l.Attribute("hits") ?? "0", CultureInfo.InvariantCulture) > 0;
                    }
                }
            }
        return lines.ToDictionary(p => p.Key, p => p.Value.Count == 0 ? 1.0 : (double)p.Value.Count(v => v.Value) / p.Value.Count);
    }

    /// <summary>Each assembly against its bar, by name.</summary>
    public static IReadOnlyList<GateResult> Gate(IReadOnlyDictionary<string, double> rates) =>
        [.. rates.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r =>
        {
            var gate = Gates.GetValueOrDefault(r.Key, DefaultGate);
            return new GateResult(r.Key, r.Value, gate, r.Value >= gate);
        })];

    /// <summary>The Markdown summary: each tier with its share of the tests, then each assembly with its gate.</summary>
    public static IReadOnlyList<string> Summary(IReadOnlyList<TierCount> tiers, IReadOnlyList<GateResult> gates, bool gated)
    {
        var all = tiers.Sum(t => t.Total);
        var lines = new List<string> { "### .NET tests", "", "| Tier | Tests | Passed |", "| --- | --- | --- |" };
        lines.AddRange(tiers.Select(t => $"| {t.Tier} | {t.Total} ({(all == 0 ? 0 : 100 * t.Total / all)}%) | {t.Passed} |"));
        lines.AddRange(["", "| Assembly | Line coverage | Gate |", "| --- | --- | --- |"]);
        lines.AddRange(gates.Select(g =>
        {
            var pct = (g.Gate * 100).ToString("0", CultureInfo.InvariantCulture);
            var mark = !gated ? "not gated" : g.Ok ? $"ok (≥ {pct}%)" : $"**FAIL** (< {pct}%)";
            return $"| {g.Assembly} | {(g.Rate * 100).ToString("0.0", CultureInfo.InvariantCulture)}% | {mark} |";
        }));
        return lines;
    }

    private static int Last(Regex re, string log) =>
        re.Matches(log) is { Count: > 0 } m ? int.Parse(m[^1].Groups[1].Value, CultureInfo.InvariantCulture) : 0;

    [GeneratedRegex(@"^\s*total:\s*(\d+)", RegexOptions.Multiline)] private static partial Regex Total();
    [GeneratedRegex(@"^\s*succeeded:\s*(\d+)", RegexOptions.Multiline)] private static partial Regex Succeeded();
    [GeneratedRegex(@"[\\/]Migrations[\\/]")] private static partial Regex Migrations();
}
