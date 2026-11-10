using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>github.ps1's GhError and PatNeeds: the text a refused GitHub call becomes (contract, "Targets").</summary>
public static partial class GitHubErrors
{
    /// <summary>The sentence a missing PAT permission becomes; deploy matches it with <see cref="PatNeedsPattern"/>.</summary>
    public static string PatNeeds(string permission) =>
        $"the GitHub PAT needs {permission} (add it at https://github.com/settings/personal-access-tokens)";

    /// <summary>github.ps1's <c>$PatNeedsPattern</c>; group 1 is the permission.</summary>
    public static Regex PatNeedsPattern => PatNeedsRegex();

    /// <summary><c>GitHub &lt;status&gt;: &lt;message&gt;</c>, trimmed, then what the PAT needs when GitHub said so.</summary>
    public static string Text(GitHubResponse r)
    {
        var msg = r.Body switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonObject o => PsJsonReader.Text(o["message"]),
            _ => "",
        };
        var text = $"GitHub {r.Status}: {msg}".Trim();
        return string.IsNullOrEmpty(r.Permission) ? text : $"{text}; {PatNeeds(r.Permission)}";
    }

    [GeneratedRegex(@"the GitHub PAT needs (\S+)", RegexOptions.IgnoreCase)] private static partial Regex PatNeedsRegex();
}
