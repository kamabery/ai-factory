using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>The text a refused GitHub call becomes (contract, "Targets").</summary>
public static partial class GitHubErrors
{
    /// <summary>The sentence a missing App permission becomes; deploy matches it with <see cref="AppNeedsPattern"/>.</summary>
    public static string AppNeeds(string permission) =>
        $"the GitHub App needs {permission} (add it in the App's settings, then accept it on the installation)";

    /// <summary>What deploy matches a missing App permission with; group 1 is the permission.</summary>
    public static Regex AppNeedsPattern => AppNeedsRegex();

    /// <summary><c>GitHub &lt;status&gt;: &lt;message&gt;</c>, trimmed, then what the App needs when GitHub said so.</summary>
    public static string Text(GitHubResponse r)
    {
        var msg = r.Body switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonObject o => PsJsonReader.Text(o["message"]),
            _ => "",
        };
        var text = $"GitHub {r.Status}: {msg}".Trim();
        return string.IsNullOrEmpty(r.Permission) ? text : $"{text}; {AppNeeds(r.Permission)}";
    }

    [GeneratedRegex(@"the GitHub App needs (\S+)", RegexOptions.IgnoreCase)] private static partial Regex AppNeedsRegex();
}
