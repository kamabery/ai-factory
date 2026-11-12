namespace Factory.Server.Abstractions;

/// <summary>serve.ps1's Invoke-Factory: one factory.ps1 call, waited for.</summary>
public interface IFactoryCalls
{
    CallResult Run(params string[] args);
}
