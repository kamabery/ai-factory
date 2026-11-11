using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>An evaluation's first line (contract, "Evaluations"): <c>Rating: good|mixed|poor</c>, exactly.</summary>
public static partial class EvalRating
{
    /// <summary>The rating, or null when the line is not one.</summary>
    public static string? Of(string? firstLine) => firstLine is not null && Line().Match(firstLine) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>A job id as eval files are named: letters, a dash, digits (factory.ps1's -match, so any case).</summary>
    public static bool IsJobId(string id) => JobId().IsMatch(id);

    [GeneratedRegex("^Rating: (good|mixed|poor)$")] private static partial Regex Line();
    [GeneratedRegex(@"^[A-Z]+-\d+$", RegexOptions.IgnoreCase)] private static partial Regex JobId();
}
