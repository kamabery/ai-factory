using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>The board mutex is factory.ps1's: the same name, so the two implementations exclude each other.</summary>
public sealed class BoardMutexTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private BoardMutex Mutex(out string home)
    {
        home = _dir[Guid.NewGuid().ToString("N")[..8]];
        return new BoardMutex(new FactoryPaths(Options.Create(new FactoryOptions { Home = home }), new GitClient(new ProcessRunner())));
    }

    /// <summary>Holds factory.ps1's mutex for <paramref name="home"/> on another thread, as another process would.</summary>
    private static (Thread Thread, ManualResetEventSlim Release) HoldAsFactoryPs1(string home, bool abandon = false)
    {
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            using var m = new System.Threading.Mutex(false, $"factory-board-{StateHash.Of(home)}");
            m.WaitOne();
            held.Set();
            if (abandon) return;
            release.Wait();
            m.ReleaseMutex();
        });
        t.Start();
        held.Wait();
        return (t, release);
    }

    [Fact]
    public void Waits_for_factory_ps1s_holder_and_times_out()
    {
        var mutex = Mutex(out var home);
        var (t, release) = HoldAsFactoryPs1(home);

        Assert.False(mutex.Enter(0));
        release.Set();
        t.Join();

        Assert.True(mutex.Enter(1));
        mutex.Exit();
    }

    [Fact]
    public void A_holder_that_died_leaves_the_mutex_to_the_next_command()
    {
        var mutex = Mutex(out var home);
        var (t, _) = HoldAsFactoryPs1(home, abandon: true);
        t.Join();

        Assert.True(mutex.Enter(1));
        mutex.Exit();
    }
}
