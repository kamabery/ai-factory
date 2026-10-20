# Ephemeral previews for factory.ps1, which dot-sources this file after hcp.ps1. See "Deploy" in
# docs/FACTORY-CONTRACT.md. These run with the board lock released while they wait on GitHub and
# HCP, the way Invoke-Gate does, so callers must re-fetch the job afterwards.

function Poll-Seconds { if ($env:FACTORY_DEPLOY_POLL_SECONDS) { [int]$env:FACTORY_DEPLOY_POLL_SECONDS } else { 15 } }
function Deploy-Deadline { (Get-Date).AddMinutes($(if ($env:FACTORY_DEPLOY_TIMEOUT_MINUTES) { [int]$env:FACTORY_DEPLOY_TIMEOUT_MINUTES } else { 60 })) }

function Preview-Env([string] $id) { 'j' + ($id -replace '\D', '') }
function Repo-Name($j) { ($j.target -split '/', 2)[1] }

# The job's deploy record, created empty on first use.
function Get-Deploy($j) {
    if (-not $j.Contains('deploy') -or -not $j.deploy) {
        $j.deploy = [ordered]@{ env = (Preview-Env $j.id); workspace = "$(Repo-Name $j)-$(Preview-Env $j.id)"; status = $null; attempts = 0
            sha = $null; run = $null; urls = $null; appliedAt = $null; destroyAt = $null }
    }
    $j.deploy
}

# gh or az, or the script FACTORY_GH_CMD / FACTORY_AZ_CMD names in their place. $vars is set in the
# environment for this one child and restored after; $stdin is piped to it.
function Invoke-Tool([string] $name, [string[]] $toolArgs, [string] $stdin = $null, [hashtable] $vars = @{}) {
    $override = if ($name -eq 'gh') { $env:FACTORY_GH_CMD } else { $env:FACTORY_AZ_CMD }
    $saved = @{}
    foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
    try {
        # Always piped, if only an empty string: a child that inherited this process's stdin would
        # otherwise wait on it for input nobody sends.
        $in = if ($stdin) { $stdin } else { '' }
        $out = if ($override) { $in | & pwsh -NoProfile -File $override @toolArgs 2>&1 } else { $in | & $name @toolArgs 2>&1 }
        $code = $LASTEXITCODE
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
    [pscustomobject]@{ Code = $code; Out = ($out | Out-String).Trim() }
}

function Set-GhVariable([string] $base, [string] $name, [string] $value) {
    $r = Invoke-GitHub POST "$base/variables" @{ name = $name; value = $value }
    if ($r.Status -eq 409) { $r = Invoke-GitHub PATCH "$base/variables/$name" @{ name = $name; value = $value } }
    if ($r.Status -notin 201, 204) { throw "cannot set $($name): $(GhError $r)" }
}

# The target's `preview` GitHub environment, its variables and secret, and Azure's trust in it.
function Prepare-GitHub($j) {
    $t = $j.target
    $c = HcpConfig
    $r = Invoke-GitHub PUT "/repos/$t/environments/preview" @{}
    if ($r.Status -ne 200) { throw "cannot create the preview environment: $(GhError $r)" }
    Set-GhVariable "/repos/$t/environments/preview" 'AZURE_CLIENT_ID' $c.azureClientId
    Set-GhVariable "/repos/$t/environments/preview" 'AZURE_TENANT_ID' $c.azureTenantId
    Set-GhVariable "/repos/$t/environments/preview" 'AZURE_SUBSCRIPTION_ID' $c.azureSubscriptionId
    Set-GhVariable "/repos/$t/actions" 'TF_CLOUD_ORGANIZATION' $c.org

    # GitHub encrypts secrets with the repo's public key (a libsodium sealed box), which gh does for us.
    # The PAT reaches gh through its environment, the token through stdin: neither is on a command line.
    if (-not $c.teamToken) { throw 'HCP_TEAM_TOKEN is not set in .env' }
    $s = Invoke-Tool gh @('secret', 'set', 'TF_API_TOKEN', '--env', 'preview', '--repo', $t) $c.teamToken @{ GH_TOKEN = (GitHubConfig).token }
    if ($s.Code -ne 0) { throw "gh secret set failed: $($s.Out)" }

    $subject = "repo:$($t):environment:preview"
    $list = Invoke-Tool az @('ad', 'app', 'federated-credential', 'list', '--id', $c.azureClientId, '--output', 'json')
    if ($list.Code -ne 0) { throw "az federated-credential list failed: $($list.Out)" }
    if (-not (@($list.Out | ConvertFrom-Json) | Where-Object { $_.subject -eq $subject })) {
        $params = @{ name = "preview-$($t -replace '[^A-Za-z0-9-]', '-')"; issuer = 'https://token.actions.githubusercontent.com'
                     subject = $subject; audiences = @('api://AzureADTokenExchange') } | ConvertTo-Json -Compress
        $made = Invoke-Tool az @('ad', 'app', 'federated-credential', 'create', '--id', $c.azureClientId, '--parameters', $params)
        if ($made.Code -ne 0) { throw "az federated-credential create failed: $($made.Out)" }
    }
}

# Dispatches deploy.yml on the job branch and waits for that run; returns it.
function Invoke-DeployRun($j, [string] $envName) {
    $t = $j.target
    $branch = "factory/$($j.id)"
    $runsPath = "/repos/$t/actions/runs?branch=$([uri]::EscapeDataString($branch))&event=workflow_dispatch"
    $before = @((Invoke-GitHub GET $runsPath).Body.workflow_runs) | ForEach-Object { [long]$_.id } | Measure-Object -Maximum
    $last = if ($before.Count) { [long]$before.Maximum } else { 0 }
    $r = Invoke-GitHub POST "/repos/$t/actions/workflows/deploy.yml/dispatches" @{ ref = $branch; inputs = @{ environment = $envName } }
    if ($r.Status -ne 204) { throw "cannot dispatch deploy.yml: $(GhError $r)" }
    $deadline = Deploy-Deadline
    $run = $null
    while ($true) {
        if (-not $run) {
            $run = @((Invoke-GitHub GET $runsPath).Body.workflow_runs) | Where-Object { [long]$_.id -gt $last } | Sort-Object { [long]$_.id } | Select-Object -First 1
        } else {
            $run = (Invoke-GitHub GET "/repos/$t/actions/runs/$($run.id)").Body
        }
        if ($run -and $run.status -eq 'completed') { return $run }
        if ((Get-Date) -gt $deadline) { throw "deploy.yml did not finish in time$(if ($run) { ": $($run.html_url)" })" }
        Start-Sleep -Seconds (Poll-Seconds)
    }
}

# The failed jobs' logs of a GitHub run, as text.
function Get-RunLog($j, $run) {
    $jobs = @((Invoke-GitHub GET "/repos/$($j.target)/actions/runs/$($run.id)/jobs").Body.jobs)
    $text = @("run $($run.html_url): $($run.conclusion)")
    foreach ($job in $jobs | Where-Object { $_.conclusion -ne 'success' }) {
        $log = Invoke-GitHub GET "/repos/$($j.target)/actions/jobs/$($job.id)/logs"
        $text += "--- job $($job.name)"
        $text += "$($log.Body)"
    }
    $text -join "`n"
}

# The whole attempt, lock released. Returns { ok, sha, run, urls, destroyAt, log }.
function Invoke-Deploy($j) {
    $d = $j.deploy
    $result = [ordered]@{ ok = $false; sha = $null; run = $null; urls = $null; destroyAt = $null; log = '' }
    $wt = WorkDir $j
    $branch = "factory/$($j.id)"
    $push = Invoke-GitAuth $wt push -q -f origin "$($branch):$branch"
    if ($push.Code -ne 0) { $result.log = "## Push`nError: push failed: $($push.Out)"; return $result }
    $result.sha = (Invoke-Git $wt rev-parse HEAD).Out

    $Mutex.ReleaseMutex()
    try {
        $wsId = $null
        try {
            $project = Ensure-HcpProject (Repo-Name $j)
            foreach ($idle in 'dev', 'staging') { Ensure-HcpWorkspace "$(Repo-Name $j)-$idle" $project | Out-Null }
            $result.destroyAt = (Get-Date).ToUniversalTime().AddHours(24).ToString('yyyy-MM-ddTHH:mm:ssZ')
            $wsId = Ensure-HcpWorkspace $d.workspace $project $result.destroyAt
            Prepare-GitHub $j
            $run = Invoke-DeployRun $j $d.env
            $result.run = $run.html_url
            if ($run.conclusion -eq 'success') {
                $outs = Get-HcpOutputs $wsId
                $result.urls = [ordered]@{ frontend = $outs['react_url'] ?? $outs['blazor_web_url']; api = $outs['api_url'] }
                $result.ok = $true
            } else {
                $result.log = "## GitHub`n$(Get-RunLog $j $run)`n`n## HCP`n$(Get-HcpRunLog $wsId)"
            }
        } catch {
            $hcp = if ($wsId) { try { "`n`n## HCP`n$(Get-HcpRunLog $wsId)" } catch { '' } } else { '' }
            $result.log = "## Factory`nError: $($_.Exception.Message)$hcp"
        }
    } finally {
        $null = $Mutex.WaitOne()
        $script:Board = Load-Board
    }
    $result
}

# The first line that reads like an error, for the event and the flag.
function First-Error([string] $log) {
    $line = ($log -split "`n" | Where-Object { $_ -match '(?i)\berror\b' } | Select-Object -First 1)
    if (-not $line) { $line = ($log -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 1) }
    ("$line".Trim() -replace '^##\[error\]', '')
}

# Starts `factory.ps1 destroy <id>` detached when the job has a preview that is not already gone.
function Start-Destroy($j) {
    if (-not $j.Contains('deploy') -or -not $j.deploy -or $j.deploy.status -in 'down', 'destroying', $null) { return }
    Start-Process pwsh -ArgumentList '-NoProfile', '-File', (Join-Path $PSScriptRoot 'factory.ps1'), 'destroy', $j.id -WindowStyle Hidden | Out-Null
}

# Destroys the preview's resources and its workspace, lock released. Returns $null or an error.
function Invoke-Destroy($j) {
    $name = $j.deploy.workspace
    $Mutex.ReleaseMutex()
    try {
        $wsId = Get-HcpWorkspaceId $name
        if ($wsId) {
            $status = Invoke-HcpDestroy $wsId (Poll-Seconds) (Deploy-Deadline)
            if ($status -ne 'applied') { return "destroy run ended $status" }
            Remove-HcpWorkspace $wsId
        }
        $null
    } catch {
        $_.Exception.Message
    } finally {
        $null = $Mutex.WaitOne()
        $script:Board = Load-Board
    }
}
