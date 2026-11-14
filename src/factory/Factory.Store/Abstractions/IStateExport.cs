namespace Factory.Store.Abstractions;

/// <summary>
/// sqlite and sql: brings board.json and events.jsonl up to the database's revision (contract, "State backends",
/// Database, Exports), under export.lock. Never throws for a file it cannot write: the next export catches up.
/// </summary>
public interface IStateExport
{
    void Export();
}
