using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>factory.ps1's Move-Job: move, save the board, append the stage event.</summary>
public static class BoardMoves
{
    public static void Move(IBoardSession session, Job job, string to, string by, TimeProvider clock, string? reason = null, string? detail = null)
    {
        var e = Transitions.Move(job, to, by, reason, detail, null, clock.GetUtcNow());
        session.Save();
        session.AddEvent(e);
    }
}
