using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary><see cref="IPause"/> as <see cref="Thread.Sleep(TimeSpan)"/>: the CLI is one command, run to its end.</summary>
public sealed class ThreadPause : IPause
{
    public void Sleep(TimeSpan time)
    {
        if (time > TimeSpan.Zero) Thread.Sleep(time);
    }
}
