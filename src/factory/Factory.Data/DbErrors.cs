using System.Data.Common;
using Factory.Core;

namespace Factory.Data;

/// <summary>
/// A database that cannot be opened or queried refuses the command in one line, as every refusal does, instead of
/// ending it with a stack trace.
/// </summary>
public static class DbErrors
{
    public static T Guard<T>(Func<T> work)
    {
        try { return work(); }
        catch (DbException e) { throw new FactoryException($"state: the database refused: {e.Message}"); }
    }

    public static void Guard(Action work) => Guard(() => { work(); return 0; });
}
