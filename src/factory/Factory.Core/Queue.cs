using System.Globalization;
using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>What canary.json says about the current fingerprint.</summary>
public enum CanaryState { None, Pass, Failed }

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

/// <summary>The Approved queue: one job builds at a time, oldest approval first (contract, "Runner" and "Canary").</summary>
public static class Queue
{
    public static bool AnyBuilding(JsonObject board) => board.Jobs().Any(j => Json.In(j.Stage, Stages.Building));

    /// <summary>A usage limit holds for every job until the human resumes the one it stopped.</summary>
    public static bool UsageLimited(JsonObject board) => board.Jobs().Any(j => Json.Is(j.Flag?.Reason, "usage-limit"));

    static IEnumerable<Job> Approved(JsonObject board) => board.Jobs()
        .Where(j => Json.Is(j.Stage, Stages.Approved))
        .OrderBy(j => j.ApprovedAt, StringComparer.OrdinalIgnoreCase)
        .ThenBy(j => j.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>factory.ps1's Next-Runnable: the queue's head, whatever the canary says.</summary>
    public static Job? NextRunnable(JsonObject board) =>
        AnyBuilding(board) || UsageLimited(board) ? null : Approved(board).FirstOrDefault();

    /// <summary>
    /// What <c>next</c> prints: the oldest approved job the runner may take, skipping deployable jobs until a
    /// canary has passed for the current fingerprint. With <paramref name="needsCanary"/>, <c>canary</c> when a
    /// deployable job is held back only because no canary has run for these inputs. Null for nothing.
    /// </summary>
    public static string? Next(JsonObject board, bool deploysOn, Func<CanaryState> canary, bool needsCanary)
    {
        if (AnyBuilding(board) || UsageLimited(board)) return null;
        CanaryState? state = null;
        foreach (var j in Approved(board))
        {
            if (!Transitions.IsDeployable(j, deploysOn)) return j.Id;
            state ??= canary();
            if (state == CanaryState.Pass) return j.Id;
            if (needsCanary && state == CanaryState.None) return "canary";
        }
        return null;
    }

    /// <summary>Whether the runner may take <paramref name="job"/>: it heads the queue, or <c>next</c> chose it past jobs held for a canary.</summary>
    public static bool IsNext(JsonObject board, Job job, bool deploysOn, Func<CanaryState> canary) =>
        Json.Is(NextRunnable(board)?.Id, job.Id) || Json.Is(Next(board, deploysOn, canary, false), job.Id);

    /// <summary>
    /// Whether <c>sweep</c> starts a destroy for a preview in this state (contract, "Deploy"): not when it is
    /// down, has failed five times (that waits for the human), or a destroy started under
    /// <paramref name="staleMinutes"/> ago may still be running.
    /// </summary>
    public static bool SweepDue(string? status, string? startedAt, int failures, DateTimeOffset now, int staleMinutes)
    {
        if (status is null || Json.Is(status, "down") || failures >= 5) return false;
        return !(Json.Is(status, "destroying") && DestroyRunning(startedAt, now, staleMinutes));
    }

    static bool DestroyRunning(string? startedAt, DateTimeOffset now, int staleMinutes) =>
        !string.IsNullOrEmpty(startedAt) &&
        DateTimeOffset.Parse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).AddMinutes(staleMinutes) > now;
}
