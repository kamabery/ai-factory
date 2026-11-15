using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary><c>factory check file &lt;path&gt; [--heading &lt;re&gt;]... [--contains &lt;text&gt;]... [--matches &lt;re&gt;]...</c>, parsed.</summary>
public sealed record FileCheckRequest(string Path, IReadOnlyList<FileCheckRule> Rules)
{
    public const string Usage = "usage: factory check file <path> [--heading <re>]... [--contains <text>]... [--matches <re>]...";

    private static readonly string[] Options = ["--heading", "--contains", "--matches"];

    /// <summary>The arguments after <c>check</c>; a usage error is a <see cref="FactoryException"/> with exit 2.</summary>
    public static FileCheckRequest Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw Refused(Usage);
        if (!args[0].Equals("file", StringComparison.OrdinalIgnoreCase)) throw Refused($"unknown check '{args[0]}' (only 'file')");
        if (args.Count < 2) throw Refused("no path to check");
        var rules = new List<FileCheckRule>();
        for (var i = 2; i < args.Count; i++)
        {
            var option = Options.FirstOrDefault(o => o.Equals(args[i], StringComparison.OrdinalIgnoreCase));
            if (option is null) throw Refused(args[i].StartsWith('-') ? $"unknown option '{args[i]}'" : $"unexpected argument '{args[i]}'");
            if (i + 1 >= args.Count) throw Refused($"{option} needs a value");
            var value = args[++i];
            if (option != "--contains")
            {
                try { _ = new Regex(value); }
                catch (ArgumentException e) { throw Refused($"{option} '{value}' is not a valid regex: {e.Message}"); }
            }
            rules.Add(new FileCheckRule(option, value));
        }
        return new FileCheckRequest(args[1], rules);
    }

    private static FactoryException Refused(string reason) => new($"check refused: {reason}", 2);
}
