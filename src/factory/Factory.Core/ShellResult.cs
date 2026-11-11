namespace Factory.Core;

/// <summary>A gate command's exit code and its output, stdout and stderr as they arrived, each line ended.</summary>
public sealed record ShellResult(int Code, string Out);
