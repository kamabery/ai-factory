namespace Factory.Store.Abstractions;

/// <summary>state.ps1's Invoke-StateAllowIp: the state storage account's firewall (contract, "State backends").</summary>
public interface IStateFirewall
{
    /// <summary>Adds this machine's public IP to the account's network rules and removes the one added last.</summary>
    AllowIpResult AllowIp();
}
