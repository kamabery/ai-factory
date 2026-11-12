using System.Globalization;
using System.Text;
using Factory.Core.Abstractions;
using Factory.Server.Abstractions;

namespace Factory.Server;

/// <summary>Small file helpers the endpoints share.</summary>
public static class ServerFiles
{
    /// <summary>The PID on a lock file's first line (runner.lock, canary.lock, turn.lock, serve.lock), or null.</summary>
    public static int? LockHolder(IFileStore files, string path)
    {
        string? text;
        try { text = files.ReadText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var first = text?.Split('\n')[0].Trim();
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    /// <summary>
    /// The request body in a temp file under the state dir for a factory.ps1 <c>-File</c> argument (written as
    /// Set-Content -NoNewline wrote it), removed afterwards; a success's message becomes <paramref name="okMessage"/>.
    /// </summary>
    public static Reply WithTempFile(string home, string name, string text, Func<string, CallResult> call, string okMessage)
    {
        var tmp = Path.Combine(home, name);
        CallResult r;
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            r = call(tmp);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
        return Reply.Of(r.Ok ? r with { Message = okMessage } : r);
    }
}
