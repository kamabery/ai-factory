namespace Factory.Store.Abstractions;

/// <summary>An external tool's exit code and its output, stdout then stderr, trimmed (PowerShell's <c>2&gt;&amp;1 | Out-String</c>).</summary>
public sealed record ToolResult(int Code, string Out)
{
    /// <summary>The output's lines, as PowerShell collects a native command's.</summary>
    public IReadOnlyList<string> Lines => Core.PsText.Lines(Out);
}
