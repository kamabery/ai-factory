# Shared by setup-platform.Tests.ps1, which tests PowerShell kept until roadmap spec 16. Dot-source it in BeforeAll, call
# Start-GitHubStub there and Stop-GitHubStub in AfterAll. The stub is Factory.TestHost's GitHub and HCP stub
# (dotnet build src/factory first), over bare repos in $script:GitBase.

function Start-GitHubStub {
    $testHost = Join-Path $PSScriptRoot '../../../artifacts/bin/Factory.TestHost/debug/Factory.TestHost.dll' | ForEach-Object { [IO.Path]::GetFullPath($_) }
    if (-not (Test-Path $testHost)) { throw "$testHost is not built (dotnet build src/factory)" }
    $script:Token = 'ghp_test_' + [guid]::NewGuid().ToString('N')
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $script:GhPort = $l.LocalEndpoint.Port; $l.Stop()
    $script:GitBase = Join-Path $TestDrive 'github'
    $hidden = if ($IsWindows) { @{ WindowStyle = 'Hidden' } } else { @{ RedirectStandardOutput = (Join-Path $TestDrive 'stub.out'); RedirectStandardError = (Join-Path $TestDrive 'stub.err') } }
    $script:GhProc = Start-Process dotnet -ArgumentList $testHost, 'github', '--port', $script:GhPort, '--git-base', $script:GitBase, '--login', 'test' -PassThru @hidden
    $deadline = (Get-Date).AddSeconds(30)
    while ($true) {
        try { Invoke-WebRequest "http://localhost:$script:GhPort/" -TimeoutSec 1 | Out-Null; break }
        catch { if ((Get-Date) -gt $deadline) { throw 'github stub did not start' }; Start-Sleep -Milliseconds 200 }
    }
    $script:EnvFile = Join-Path $TestDrive 'github.env'
    Set-Content $script:EnvFile @("GITHUB_TOKEN=$script:Token", 'GITHUB_OWNER=test', "GITHUB_API_URL=http://localhost:$script:GhPort",
        "FACTORY_GIT_BASE=file:///$($script:GitBase -replace '\\', '/')")
    # The same, with deploys on: HCP's API is served by the same stub. See "Deploy" in the contract.
    $script:DeployEnvFile = Join-Path $TestDrive 'deploy.env'
    Set-Content $script:DeployEnvFile ((Get-Content $script:EnvFile) + @(
        'HCP_ORG=test-org', "HCP_TOKEN=hcp_test_$([guid]::NewGuid().ToString('N'))", 'HCP_TEAM_TOKEN=team-token-for-actions', 'HCP_AZURE_VARSET=varset-azure',
        'AZURE_GH_CLIENT_ID=gh-app-client', 'AZURE_TENANT_ID=tenant-1', 'AZURE_SUBSCRIPTION_ID=sub-1',
        "HCP_API_URL=http://localhost:$script:GhPort"))
}

function Stop-GitHubStub {
    if ($script:GhProc -and -not $script:GhProc.HasExited) { Stop-Process -Id $script:GhProc.Id -Force }
}
