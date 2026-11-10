using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>Board ids: <c>I-NNN</c> and <c>J-NNN</c>, one above the larger of <c>seq</c> and the highest id on the board.</summary>
public static partial class Ids
{
    /// <summary>Allocates the next id and records it in <c>board.seq</c>, so a purged id is never reused.</summary>
    public static string Next(JsonObject board, string prefix, JsonArray items)
    {
        var n = UpdateSeq(board, prefix, items) + 1;
        ((JsonObject)board["seq"]!)[prefix] = n;
        return $"{prefix}-{n:D3}";
    }

    /// <summary>factory.ps1's Update-Seq: raises <c>board.seq[prefix]</c> to the highest id on the board, and returns it.</summary>
    public static int UpdateSeq(JsonObject board, string prefix, JsonArray items)
    {
        if (board["seq"] is not JsonObject seq) board["seq"] = seq = [];
        var max = PsJsonReader.Int(seq[prefix]);
        var id = new Regex($"^{Regex.Escape(prefix)}-(\\d+)$", RegexOptions.IgnoreCase);
        foreach (var x in items)
            if (id.Match(PsJsonReader.Text(x?["id"])) is { Success: true } m) max = Math.Max(max, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        seq[prefix] = max;
        return max;
    }
}
