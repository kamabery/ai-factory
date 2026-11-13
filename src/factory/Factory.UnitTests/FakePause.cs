using Factory.Store.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Factory.UnitTests;

/// <summary>An <see cref="IPause"/> that passes the fake clock's time instead of sleeping, and records each wait.</summary>
public sealed class FakePause(FakeTimeProvider clock) : IPause
{
    public List<TimeSpan> Sleeps { get; } = [];

    /// <summary>Runs before each wait returns: what the world does meanwhile.</summary>
    public Action? Meanwhile { get; set; }

    public void Sleep(TimeSpan time)
    {
        Sleeps.Add(time);
        clock.Advance(time);
        Meanwhile?.Invoke();
    }
}
