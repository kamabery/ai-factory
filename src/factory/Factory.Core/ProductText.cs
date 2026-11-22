using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>Reads and checks a product document, <c>factory/templates/product.md</c> (contract, "Ideas", "A New repo idea").</summary>
public static partial class ProductText
{
    public const int MaxFeatures = 10;

    public static Product Parse(string? text)
    {
        var lines = PsText.Lines(text);
        string? title = null;
        var features = new List<ProductFeature>();
        var inFeatures = false;
        string? heading = null;
        var body = new List<string>();
        void Flush()
        {
            if (heading is not null) features.Add(new ProductFeature(heading, string.Join('\n', body).Trim()));
            heading = null;
            body.Clear();
        }
        foreach (var line in lines)
        {
            if (title is null && Title().Match(line) is { Success: true } t) { title = t.Groups[1].Value; continue; }
            if (Section().Match(line) is { Success: true } s)
            {
                Flush();
                inFeatures = string.Equals(s.Groups[1].Value, "Features", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inFeatures && Feature().Match(line) is { Success: true } f) { Flush(); heading = f.Groups[1].Value; continue; }
            if (heading is not null) body.Add(line);
        }
        Flush();
        var home = SpecText.HomeDecision(lines) is { } h ? $"- Home: {h}" : null;
        return new Product(title, home, features);
    }

    /// <summary>Why a product document is refused, or null.</summary>
    public static string? Error(string? text)
    {
        var p = Parse(text);
        if (p.Title is null) return "product refused: the first line must be '# Product: <app name>'";
        if (p.Home is null) return "product refused: ## Home needs a '- Home: replace|redirect|keep: <detail>' line";
        if (p.Features.Count is < 1 or > MaxFeatures)
            return $"product refused: list 1 to {MaxFeatures} features, each a ### heading under ## Features (it has {p.Features.Count})";
        for (var i = 0; i < p.Features.Count; i++)
            if (p.Features.Take(i).Any(f => SameHeading(f.Heading, p.Features[i].Heading)))
                return $"product refused: feature '{p.Features[i].Heading.Trim()}' is listed twice";
        return null;
    }

    /// <summary>A feature is matched by its heading, without case and surrounding space.</summary>
    public static bool SameHeading(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^#\s+Product:\s*(.+?)\s*$", RegexOptions.IgnoreCase)] private static partial Regex Title();
    [GeneratedRegex(@"^##\s+(.*?)\s*$")] private static partial Regex Section();
    [GeneratedRegex(@"^###\s+(.*?)\s*$")] private static partial Regex Feature();
}
