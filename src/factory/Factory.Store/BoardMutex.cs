using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// factory.ps1's board mutex, <c>factory-board-&lt;hash of FACTORY_HOME&gt;</c>. A named mutex works across .NET
/// processes on every OS, so every factory command on one state dir excludes the others (it once kept factory.ps1 out too).
/// </summary>
public sealed class BoardMutex(IFactoryPaths paths) : IBoardMutex, IDisposable
{
    private Mutex? _mutex;

    public bool Enter(int seconds)
    {
        _mutex ??= new Mutex(false, $"factory-board-{StateHash.Of(paths.Home)}");
        // A holder that died leaves the mutex abandoned, and ours.
        try { return _mutex.WaitOne(seconds < 0 ? Timeout.Infinite : seconds * 1000); }
        catch (AbandonedMutexException) { return true; }
    }

    public void Exit() => _mutex?.ReleaseMutex();

    public void Dispose() => _mutex?.Dispose();
}
