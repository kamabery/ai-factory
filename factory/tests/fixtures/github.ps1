# Shared by the Pester files whose jobs have GitHub targets. Dot-source it in BeforeAll, call
# Start-GitHubStub there and Stop-GitHubStub in AfterAll, and point FACTORY_ENV_FILE at $script:EnvFile.
# Targets are bare repos in $script:GitBase, served by github-stub.ps1 and cloned by factory.ps1 over
# file:// (git ignores --depth for a plain local path).

function Start-GitHubStub {
    $stub = Join-Path $PSScriptRoot 'github-stub.ps1'
    $script:Token = 'ghp_test_' + [guid]::NewGuid().ToString('N')
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $script:GhPort = $l.LocalEndpoint.Port; $l.Stop()
    $script:GitBase = Join-Path $TestDrive 'github'
    $script:GitUrl = 'file:///' + ($script:GitBase -replace '\\', '/')
    $script:GhProc = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $stub, '-Port', $script:GhPort, '-GitBase', $script:GitBase, '-Login', 'test' -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(20)
    while ($true) {
        try { Invoke-WebRequest "http://localhost:$script:GhPort/" -TimeoutSec 1 | Out-Null; break }
        catch { if ((Get-Date) -gt $deadline) { throw 'github stub did not start' }; Start-Sleep -Milliseconds 200 }
    }
    $script:EnvFile = Join-Path $TestDrive 'github.env'
    Set-Content $script:EnvFile @("GITHUB_TOKEN=$script:Token", 'GITHUB_OWNER=test', "GITHUB_API_URL=http://localhost:$script:GhPort", "FACTORY_GIT_BASE=$script:GitUrl")

    # The same, with deploys on: HCP's API is served by the same stub. See "Deploy" in the contract.
    $script:HcpToken = 'hcp_test_' + [guid]::NewGuid().ToString('N')
    $script:DeployEnvFile = Join-Path $TestDrive 'deploy.env'
    Set-Content $script:DeployEnvFile ((Get-Content $script:EnvFile) + @(
        'HCP_ORG=test-org', "HCP_TOKEN=$script:HcpToken", 'HCP_TEAM_TOKEN=team-token-for-actions', 'HCP_AZURE_VARSET=varset-azure',
        'AZURE_GH_CLIENT_ID=gh-app-client', 'AZURE_TENANT_ID=tenant-1', 'AZURE_SUBSCRIPTION_ID=sub-1',
        "HCP_API_URL=http://localhost:$script:GhPort"))
}

# Deploys on, with gh and az replaced by a stub that records each call in $env:FACTORY_TOOL_LOG.
function Use-Deploys {
    $env:FACTORY_ENV_FILE = $script:DeployEnvFile
    $stub = Join-Path $PSScriptRoot 'tool-stub.ps1'
    $env:FACTORY_GH_CMD = $stub
    $env:FACTORY_AZ_CMD = $stub
    $env:FACTORY_TOOL_LOG = Join-Path $TestDrive "tools-$([guid]::NewGuid().ToString('N').Substring(0,8)).jsonl"
    $env:FACTORY_DEPLOY_POLL_SECONDS = '0'
}

function Clear-Deploys { Remove-Item Env:FACTORY_GH_CMD, Env:FACTORY_AZ_CMD, Env:FACTORY_TOOL_LOG, Env:FACTORY_DEPLOY_POLL_SECONDS -ErrorAction SilentlyContinue }

function ToolCalls { if (Test-Path $env:FACTORY_TOOL_LOG) { Get-Content $env:FACTORY_TOOL_LOG | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } } }

# The stub's Actions and HCP state, shared by every test (names carry the repo, so tests do not collide).
function DeployState { Get-Content (Join-Path $script:GitBase 'deploy-state.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
function HcpWorkspace([string]$name) { (DeployState).workspaces.Values | Where-Object { $_.name -eq $name } | Select-Object -First 1 }

function Stop-GitHubStub {
    if ($script:GhProc -and -not $script:GhProc.HasExited) { Stop-Process -Id $script:GhProc.Id -Force }
}

function Bare([string]$repo = $script:Repo) { Join-Path $script:GitBase "$repo.git" }
function RGit { git -C (Bare) @args }
function Work([string]$id) { Join-Path $env:FACTORY_WORK "jobs/$id" }

# The stub's request log for one repo (the stub is shared by every test): its /repos calls and its create.
function GhRequests([string]$repo = $script:Repo) {
    $name = ($repo -split '/')[1]
    Get-Content (Join-Path $script:GitBase 'requests.jsonl') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } |
        Where-Object { $_.path -eq "/repos/$repo" -or $_.path.StartsWith("/repos/$repo/") -or ($_.path -match '/repos$' -and $_.body.name -eq $name) }
}

# A GitHub repo with one commit on main, as the stub serves it.
function New-Remote([string]$repo) {
    $seed = Join-Path $TestDrive "seed-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    git init -q -b main $seed 2>&1 | Out-Null
    Set-Content (Join-Path $seed 'README.md') 'fixture'
    git -C $seed add -A 2>&1 | Out-Null
    git -C $seed -c user.email=f@t -c user.name=f commit -q -m init 2>&1 | Out-Null
    git clone -q --bare $seed (Bare $repo) 2>&1 | Out-Null
}

# Pushes a commit straight to the remote's main, as another contributor would.
function Push-ToMain([string]$file, [string]$text) {
    $c = Join-Path $TestDrive "other-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    git clone -q (Bare) $c 2>&1 | Out-Null
    Set-Content (Join-Path $c $file) $text
    git -C $c add -A 2>&1 | Out-Null
    git -C $c -c user.email=o@t -c user.name=o commit -q -m "other: $file" 2>&1 | Out-Null
    git -C $c push -q origin main 2>&1 | Out-Null
}
