using System.Text.Json.Nodes;

namespace Factory.Core;

public static class Canary
{
    /// <summary>
    /// <c>Pass</c> or <c>Failed</c> when canary.json records that for the current fingerprint, else <c>None</c>
    /// (never run, run for other inputs, or still running). The fingerprint is only computed when there is a record.
    /// </summary>
    public static CanaryState State(JsonObject? canary, Func<string> fingerprint)
    {
        if (canary is null || !PsJsonReader.Is(PsJsonReader.Str(canary["fingerprint"]), fingerprint())) return CanaryState.None;
        var status = PsJsonReader.Str(canary["status"]);
        return PsJsonReader.Is(status, "pass") ? CanaryState.Pass : PsJsonReader.Is(status, "failed") ? CanaryState.Failed : CanaryState.None;
    }

    private static readonly (string Group, string Label)[] Groups =
        [("deploy", "deploy code"), ("templates", "templates"), ("varset", "variable set"), ("settings", "canary settings")];

    /// <summary>Why a canary is due, against the newest pass in <c>passes</c> (contract, "Canary", "Why it is due").</summary>
    public static IReadOnlyList<string> Reasons(JsonObject? canary, IReadOnlyDictionary<string, string> inputs, string version)
    {
        if (Passes(canary).FirstOrDefault() is not { } last)
            return canary is not null && PsJsonReader.Is(PsJsonReader.Str(canary["status"]), "pass")
                ? [$"inputs changed since pass #{PsJsonReader.Int(canary["n"])}"]
                : ["no pass on record"];
        var n = PsJsonReader.Int(last["n"]);
        var was = last["inputs"] as JsonObject;
        var reasons = new List<string>();
        foreach (var (group, label) in Groups)
        {
            if (PsJsonReader.Is(PsJsonReader.Str(was?[group]), inputs.GetValueOrDefault(group))) continue;
            var old = PsJsonReader.Str(last["version"]);
            reasons.Add(group == "deploy" && !string.IsNullOrEmpty(old) && !string.IsNullOrEmpty(version) && !PsJsonReader.Is(old, version)
                ? $"factory {old} → {version}"
                : $"{label} changed since pass #{n}");
        }
        return reasons;
    }

    /// <summary>
    /// Due: deploys on, nothing running, and no pass recorded for the current fingerprint (a failed or retry record
    /// for it is due too). The fingerprint is computed only when that could change the answer.
    /// </summary>
    public static bool Due(JsonObject? canary, Func<string> fingerprint, bool deploysOn)
    {
        if (!deploysOn) return false;
        if (canary is null) return true;
        var status = PsJsonReader.Str(canary["status"]);
        if (PsJsonReader.Is(status, "running")) return false;
        return !(PsJsonReader.Is(status, "pass") && PsJsonReader.Is(PsJsonReader.Str(canary["fingerprint"]), fingerprint()));
    }

    /// <summary>The median of the recorded passes' durations in whole minutes, or 15 with fewer than three.</summary>
    public static int TypicalMinutes(JsonObject? canary)
    {
        var minutes = Passes(canary)
            .Select(p => (DateTimeOffset.TryParse(PsJsonReader.Str(p["startedAt"]), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var s),
                          DateTimeOffset.TryParse(PsJsonReader.Str(p["finishedAt"]), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var f), s, f))
            .Where(x => x.Item1 && x.Item2)
            .Select(x => (x.f - x.s).TotalMinutes)
            .Order().ToList();
        if (minutes.Count < 3) return 15;
        var mid = minutes.Count / 2;
        var median = minutes.Count % 2 == 1 ? minutes[mid] : (minutes[mid - 1] + minutes[mid]) / 2;
        return (int)Math.Floor(median);
    }

    private static IEnumerable<JsonObject> Passes(JsonObject? canary) =>
        canary?["passes"] is JsonArray a ? a.OfType<JsonObject>() : [];
}
