using Factory.Core.Abstractions;

namespace Factory.UnitTests;

/// <summary>
/// A scripted <see cref="IGitClient"/>: each call is recorded, and answered by the last rule whose text the call's
/// arguments (joined by spaces) contain; anything else exits 0 with no output.
/// </summary>
public sealed class FakeGit : IGitClient
{
    private readonly List<(string Text, Func<GitResult> Answer)> _rules = [];

    public List<(string Dir, string Args)> Calls { get; } = [];

    public FakeGit On(string text, int code, string output = "") => On(text, () => new GitResult(code, output));

    public FakeGit On(string text, Func<GitResult> answer)
    {
        _rules.Add((text, answer));
        return this;
    }

    public string? CommonDir(string workingDirectory) => null;

    public GitResult Run(string directory, params string[] args)
    {
        var joined = string.Join(' ', args);
        Calls.Add((directory, joined));
        for (var i = _rules.Count - 1; i >= 0; i--)
            if (joined.Contains(_rules[i].Text, StringComparison.Ordinal)) return _rules[i].Answer();
        return new GitResult(0, "");
    }

    /// <summary>The calls whose arguments contain <paramref name="text"/>.</summary>
    public List<(string Dir, string Args)> Ran(string text) => [.. Calls.Where(c => c.Args.Contains(text, StringComparison.Ordinal))];
}
