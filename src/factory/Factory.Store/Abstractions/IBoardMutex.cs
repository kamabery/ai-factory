namespace Factory.Store.Abstractions;

/// <summary>The machine's board lock, shared with factory.ps1.</summary>
public interface IBoardMutex
{
    /// <summary>Waits up to <paramref name="seconds"/> (-1: as long as it takes); false when it timed out.</summary>
    bool Enter(int seconds);

    void Exit();
}
