using System.Globalization;
using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>Numbers as PowerShell 7's <c>ConvertTo-Json</c> writes them.</summary>
public static class PsNumber
{
    /// <summary>A double: the shortest round-trip form, and <c>.0</c> on a whole number (<c>1.0</c>, <c>0.3</c>, <c>1E-05</c>).</summary>
    public static JsonNode Double(double value)
    {
        if (!double.IsFinite(value)) throw new FactoryException($"{value.ToString(CultureInfo.InvariantCulture)} is not a number JSON can hold");
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E')) s += ".0";
        return JsonNode.Parse(s)!;
    }
}
