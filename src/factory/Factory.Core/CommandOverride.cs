using System.Text.Json;

namespace Factory.Core;

/// <summary>
/// A test override (FACTORY_GH_CMD, FACTORY_AZ_CMD, FACTORY_SIGNIN_CMD, FACTORY_CANARY_SCAFFOLD_CMD, FACTORY_BROWSER_CMD,
/// FACTORY_IDEA_CLAUDE_CMD, FACTORY_CLAUDE_CMD; contract, "Implementations"): a program and its leading arguments, run with
/// the real one's arguments after them and no shell between. A JSON array of strings, or else a path, the program alone.
/// </summary>
public static class CommandOverride
{
    /// <summary>The program, then its leading arguments.</summary>
    public static IReadOnlyList<string> Parse(string name, string value)
    {
        var v = value.Trim();
        if (!v.StartsWith('[')) return [value];
        try
        {
            if (JsonSerializer.Deserialize<string?[]>(v) is { Length: > 0 } a && a.All(s => s is not null) && a[0]!.Length > 0) return [.. a!];
        }
        catch (JsonException) { }
        throw new FactoryException($"{name} must be a JSON array of strings, the program first, or a path");
    }
}
