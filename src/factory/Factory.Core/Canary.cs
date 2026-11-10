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
        if (canary is null || !Json.Is(Json.Str(canary["fingerprint"]), fingerprint())) return CanaryState.None;
        var status = Json.Str(canary["status"]);
        return Json.Is(status, "pass") ? CanaryState.Pass : Json.Is(status, "failed") ? CanaryState.Failed : CanaryState.None;
    }
}
