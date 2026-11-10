namespace Factory.Core;

/// <summary>Text files as PowerShell reads and writes them: Get-Content's lines and Set-Content's newlines.</summary>
public static class PsText
{
    /// <summary>Get-Content: the lines, split on CRLF, LF or CR, with no empty line for a final newline.</summary>
    public static IReadOnlyList<string> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Set-Content: every line, the last one too, ends with <paramref name="newLine"/>.</summary>
    public static string Content(IEnumerable<string> lines, string newLine) => string.Concat(lines.Select(l => l + newLine));
}
