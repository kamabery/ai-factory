using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>A bootstrap's (and the canary's) scaffold options (contract, "Targets"): factory.ps1's Test-Scaffold.</summary>
public static partial class Scaffold
{
    private static readonly string[] Dbs = ["sqlite", "sqlserver", "postgres"];
    private static readonly string[] Uis = ["react", "blazor", "blazor-maui", "none"];

    /// <summary>Null for valid options, or why they are refused. Name, resource, db and ui compare with case.</summary>
    public static string? Error(JsonNode? sc)
    {
        if (sc is not JsonObject o) return "the scaffold options must be a JSON object";
        var name = PsJsonReader.Text(o["name"]);
        var resource = PsJsonReader.Text(o["resource"]);
        if (!Name().IsMatch(name)) return $"scaffold name '{name}' must be lower-case letters and digits, starting with a letter";
        if (!Resource().IsMatch(resource)) return $"scaffold resource '{resource}' must be PascalCase";
        if (PsJsonReader.Str(o["db"]) is not { } db || !Dbs.Contains(db, StringComparer.Ordinal)) return $"scaffold db '{PsJsonReader.Text(o["db"])}' must be sqlite, sqlserver or postgres";
        if (PsJsonReader.Str(o["ui"]) is not { } ui || !Uis.Contains(ui, StringComparer.Ordinal)) return $"scaffold ui '{PsJsonReader.Text(o["ui"])}' must be react, blazor, blazor-maui or none";
        if (o.ContainsKey("events") && !IsBool(o["events"])) return "scaffold events must be true or false";
        if (o.ContainsKey("auth") && !IsBool(o["auth"])) return "scaffold auth must be true or false";
        if (o.ContainsKey("auth") && PsJsonReader.Truthy(o["auth"]) && PsJsonReader.Is(ui, "none"))
            return "scaffold auth needs a web front end (react, blazor or blazor-maui) for sign-in to redirect back to";
        return null;
    }

    /// <summary>
    /// factory.ps1's Scaffold-Commands: the commands that scaffold a project from the templates, in order. A
    /// bootstrap job's builder and the canary run the same ones; auth comes after the front end it reads.
    /// </summary>
    public static IReadOnlyList<string> Commands(JsonObject sc)
    {
        List<string> cmds = [$"dotnet new aspire-base -n {PsJsonReader.Text(sc["name"])} -o . --dbProvider {PsJsonReader.Text(sc["db"])} --resource {PsJsonReader.Text(sc["resource"])}"];
        var ui = PsJsonReader.Str(sc["ui"]);
        if (PsJsonReader.Is(ui, "react")) cmds.Add("aspire-addon add ui-react");
        else if (PsJsonReader.Is(ui, "blazor")) cmds.Add("aspire-addon add ui-blazor --web true --maui false");
        else if (PsJsonReader.Is(ui, "blazor-maui")) cmds.Add("aspire-addon add ui-blazor --web true --maui true");
        if (PsJsonReader.Truthy(sc["events"])) cmds.Add("aspire-addon add events");
        if (PsJsonReader.Truthy(sc["auth"])) cmds.Add("aspire-addon add auth");
        return cmds;
    }

    private static bool IsBool(JsonNode? n) => n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    [GeneratedRegex("^[a-z][a-z0-9]*$")] private static partial Regex Name();
    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")] private static partial Regex Resource();
}
