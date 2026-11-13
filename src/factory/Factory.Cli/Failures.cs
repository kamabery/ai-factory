using System.Text.Json;
using Factory.Core;

namespace Factory.Cli;

/// <summary>
/// The failures a deploy or canary step can meet on the way (a refusal, GitHub, HCP, the disk, bad JSON), which become a
/// failed attempt's log, as deploy.ps1's <c>catch</c> made them. Anything else is a bug and is left to crash.
/// </summary>
internal static class Failures
{
    public static bool Expected(Exception e) =>
        e is FactoryException or HttpRequestException or IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or TaskCanceledException or FormatException;
}
