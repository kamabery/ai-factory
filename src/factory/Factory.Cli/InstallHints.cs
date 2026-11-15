namespace Factory.Cli;

/// <summary>This OS's install command for each tool doctor and init check (macOS is out of scope).</summary>
public static class InstallHints
{
    public static string For(string tool, bool windows) => (tool, windows) switch
    {
        ("git", true) => "winget install --id Git.Git -e",
        ("git", false) => "sudo apt-get install -y git",
        ("dotnet", true) => "winget install --id Microsoft.DotNet.SDK.10 -e",
        ("dotnet", false) => "curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0",
        ("claude", true) => "irm https://claude.ai/install.ps1 | iex",
        ("claude", false) => "curl -fsSL https://claude.ai/install.sh | bash",
        ("az", true) => "winget install --id Microsoft.AzureCLI -e",
        ("az", false) => "curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash",
        ("terraform", true) => "winget install --id Hashicorp.Terraform -e",
        ("terraform", false) => "see https://developer.hashicorp.com/terraform/install",
        _ => $"install {tool}",
    };
}
