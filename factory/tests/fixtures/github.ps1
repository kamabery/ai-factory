# Shared by the Pester files whose jobs have GitHub targets. Dot-source it in BeforeAll, call
# Start-GitHubStub there and Stop-GitHubStub in AfterAll, and point FACTORY_ENV_FILE at $script:EnvFile.
# Targets are bare repos in $script:GitBase, served by github-stub.ps1 and cloned by factory.ps1 over
# file:// (git ignores --depth for a plain local path).

. (Join-Path $PSScriptRoot 'blob.ps1')   # FACTORY_TEST_STATE=azure (see Start-GitHubStub)

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

    # FACTORY_TEST_STATE=azure runs the suite on the azure state backend: a blob stub, one container
    # per FACTORY_HOME (see "State backends" in the contract).
    if ($env:FACTORY_TEST_STATE -eq 'azure') {
        Start-BlobStub
        $env:FACTORY_STATE = 'azure'
        $env:FACTORY_BLOB_URL = "http://localhost:$script:BlobPort/{home}"
        $env:FACTORY_STATE_TOKEN = 'test-token'
        $env:FACTORY_LOCAL_DIR = Join-Path $TestDrive 'local'
    }
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

function Clear-Deploys {
    Remove-Item Env:FACTORY_GH_CMD, Env:FACTORY_AZ_CMD, Env:FACTORY_TOOL_LOG, Env:FACTORY_DEPLOY_POLL_SECONDS,
        Env:FACTORY_TEMPLATES, Env:FACTORY_CANARY_SCAFFOLD_CMD, Env:FACTORY_SMOKE_SECONDS, Env:CANARY_DEPLOY_FAIL -ErrorAction SilentlyContinue
}

# After Use-Deploys: what a canary needs (see "Canary" in the contract) - its own repo, a template
# workspace for the fingerprint, and a scaffold command that writes a README, plus deploy-fail.txt
# when $env:CANARY_DEPLOY_FAIL is set. Sets $script:CanaryRepo.
function Use-Canary {
    $script:CanaryRepo = "test/canary-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $envFile = Join-Path $TestDrive "canary-$([guid]::NewGuid().ToString('N').Substring(0,8)).env"
    Set-Content $envFile ((Get-Content $env:FACTORY_ENV_FILE) + "FACTORY_CANARY_REPO=$script:CanaryRepo")
    $env:FACTORY_ENV_FILE = $envFile
    $env:FACTORY_TEMPLATES = Join-Path $TestDrive "templates-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Force "$env:FACTORY_TEMPLATES/IaC", "$env:FACTORY_TEMPLATES/base-project/.github/workflows", "$env:FACTORY_TEMPLATES/artifacts/nupkg" | Out-Null
    Set-Content "$env:FACTORY_TEMPLATES/IaC/providers.tf" 'provider "azurerm" {}'
    Set-Content "$env:FACTORY_TEMPLATES/base-project/.github/workflows/deploy.yml" 'name: deploy'
    Set-Content "$env:FACTORY_TEMPLATES/artifacts/nupkg/AspireStarter.Templates.1.0.0.nupkg" 'pack'
    $env:FACTORY_CANARY_SCAFFOLD_CMD = Join-Path $TestDrive 'canary-scaffold.ps1'
    Set-Content $env:FACTORY_CANARY_SCAFFOLD_CMD @(
        'Set-Content README.md "canary scaffold"'
        'if ($env:CANARY_DEPLOY_FAIL) { Set-Content deploy-fail.txt $env:CANARY_DEPLOY_FAIL }'
    )
    $env:FACTORY_SMOKE_SECONDS = '0'
}

function ToolCalls { if (Test-Path $env:FACTORY_TOOL_LOG) { Get-Content $env:FACTORY_TOOL_LOG | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable } } }

# The stub's Actions and HCP state, shared by every test (names carry the repo, so tests do not collide).
function DeployState { Get-Content (Join-Path $script:GitBase 'deploy-state.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
function HcpWorkspace([string]$name) { (DeployState).workspaces.Values | Where-Object { $_.name -eq $name } | Select-Object -First 1 }

function Stop-GitHubStub {
    if ($script:GhProc -and -not $script:GhProc.HasExited) { Stop-Process -Id $script:GhProc.Id -Force }
    if ($env:FACTORY_TEST_STATE -eq 'azure') { Stop-BlobStub; Remove-Item Env:FACTORY_STATE, Env:FACTORY_BLOB_URL, Env:FACTORY_STATE_TOKEN, Env:FACTORY_LOCAL_DIR -ErrorAction SilentlyContinue }
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
