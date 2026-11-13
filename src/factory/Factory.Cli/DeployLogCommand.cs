using System.Globalization;
using System.Text.RegularExpressions;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>deploy-log &lt;id&gt;</c>: the last failed attempt's deploy-&lt;a&gt;.log, for the devops agent's diagnosis and the approver.</summary>
public sealed partial class DeployLogCommand(IFactoryPaths paths, IFileStore files) : ICommand
{
    public string Name => "deploy-log";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        var dir = Path.GetFullPath(paths.JobDir(j.Id));
        var last = files.Files(dir)
            .Where(f => string.Equals(Path.GetDirectoryName(Path.GetFullPath(f.Path)), dir, StringComparison.OrdinalIgnoreCase) && LogName().IsMatch(Path.GetFileName(f.Path)))
            .OrderBy(f => long.TryParse(Digits().Replace(Path.GetFileNameWithoutExtension(f.Path), ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .LastOrDefault() ?? throw new FactoryException($"{j.Id} has no failed deploy");
        var text = files.ReadText(last.Path) ?? "";
        // Get-Content -Raw, then the host's own newline: the text as written, its trailing newline included.
        return CommandResult.Ok(text);
    }

    [GeneratedRegex(@"^deploy-.*\.log$", RegexOptions.IgnoreCase)] private static partial Regex LogName();
    [GeneratedRegex(@"\D")] private static partial Regex Digits();
}
