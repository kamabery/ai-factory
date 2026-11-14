using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>canary.ps1's Lock-Canary and its azure lease: a <see cref="LeasedLock"/> on <c>canary.lock</c> and <c>locks/canary</c>.</summary>
public sealed class CanaryLock(IFactoryPaths paths, IStateLeases leases, IOptions<StateOptions> options) : ICanaryLock
{
    public IDisposable? TryTake(out bool otherMachine) =>
        new LeasedLock(Path.Combine(paths.Home, "canary.lock"), "canary", leases, options).TryTake(out otherMachine);
}
