using System.Globalization;

namespace Factory.Core;

/// <summary>
/// deploy.ps1's Destroy-Running and Sweep-Due: which previews <c>sweep</c> tears down (contract, "Deploy"). Only a
/// finished job's preview is swept; a non-terminal job's next round applies over its preview, and a destroy there is
/// the J-022 race. HCP's auto-destroy fires once, so it is no backstop for a destroy that errors: sweep is.
/// </summary>
public static class Sweep
{
    /// <summary>A preview whose destroy failed this many times waits for the human.</summary>
    public const int MaxFailures = 5;

    /// <summary>True while a destroy that started at <paramref name="startedAt"/> may still be running.</summary>
    public static bool DestroyRunning(string? startedAt, DateTimeOffset now, int staleMinutes) =>
        !string.IsNullOrEmpty(startedAt)
        && DateTimeOffset.Parse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).AddMinutes(staleMinutes) > now;

    /// <summary>Whether sweep should start a destroy for a preview in this state.</summary>
    public static bool Due(string? status, string? startedAt, int failures, DateTimeOffset now, int staleMinutes)
    {
        if (status is null || PsJsonReader.Is(status, "down") || failures >= MaxFailures) return false;
        return !(PsJsonReader.Is(status, "destroying") && DestroyRunning(startedAt, now, staleMinutes));
    }
}
