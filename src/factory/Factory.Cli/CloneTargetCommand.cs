using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>clone-target &lt;owner/name&gt; &lt;absolute dir&gt; [-Base &lt;J-NNN&gt;]</c>: replaces the folder with a <c>--depth 1</c> clone
/// of main, or of the unmerged base's branch; prints the folder.
/// </summary>
public sealed class CloneTargetCommand(IGitHub github, IGitClient git, IFileStore files) : ICommand
{
    private const string Usage = "usage: clone-target <owner/name> <absolute dir> [-Base <J-NNN>]";

    public string Name => "clone-target";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var t = args.Arg(1);
        var dir = args.Arg(2);
        if (SpecText.TargetError(t) is { } err) throw new FactoryException($"clone-target refused: {err}");
        if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) throw new FactoryException(Usage);
        var @base = args.Named("Base");
        if (@base.Length > 0 && MergeRules.BaseError(session.Board, t, @base) is { } why) throw new FactoryException($"base refused: {why}");
        // A folder that will not go (a locked file) is left for git to refuse, as Remove-Item -ErrorAction SilentlyContinue did.
        try { files.DeleteDirectory(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var parent = Path.GetDirectoryName(dir)!;
        files.EnsureDirectory(parent);
        var r = git.Run(parent, [.. github.GitAuth(), "clone", "-q", "--depth", "1", "--branch", @base.Length > 0 ? $"factory/{@base}" : "main", github.CloneUrl(t), dir]);
        if (r.Code != 0) throw new FactoryException($"clone of {t} failed: {r.Out}");
        return CommandResult.Ok(dir);
    }
}
