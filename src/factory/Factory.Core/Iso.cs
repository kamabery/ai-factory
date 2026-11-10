using System.Globalization;

namespace Factory.Core;

/// <summary>The contract's times: UTC, milliseconds, <c>Z</c> (factory.ps1's <c>Now</c>).</summary>
public static class Iso
{
    public static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
