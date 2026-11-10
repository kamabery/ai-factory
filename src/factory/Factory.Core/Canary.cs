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
}
