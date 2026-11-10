namespace Factory.Store.Abstractions;

/// <summary>The Entra bearer token for Blob storage (contract, "State backends", Auth).</summary>
public interface IStateToken
{
    string Get();
}
