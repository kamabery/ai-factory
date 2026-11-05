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
        $j.deploy = [ordered]@{ env = (Preview-Env $j.id); workspace = "$(Repo-Name $j)-$(Preview-Env $j.id)"; workspaceId = $null; status = $null; attempts = 0
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
    if ($s.Code -ne 0) {
        $why = if ($s.Out -match 'HTTP 403') { "; $(PatNeeds 'secrets=write')" } else { '' }
        throw "gh secret set failed: $($s.Out)$why"
    }

    # Repos on GitHub's immutable subject (the default for new ones) present repo:<owner>@<id>/<name>@<id>.
    $oidc = Invoke-GitHub GET "/repos/$t/actions/oidc/customization/sub"
    if ($oidc.Status -ne 200) { throw "cannot read the OIDC subject format: $(GhError $oidc)" }
    $p = $oidc.Body.PSObject.Properties
    $prefix = if ($p['use_immutable_subject']?.Value -and $p['sub_claim_prefix']?.Value) { $p['sub_claim_prefix'].Value } else { "repo:$t" }
    $subject = "$($prefix):environment:preview"
    $list = Invoke-Tool az @('ad', 'app', 'federated-credential', 'list', '--id', $c.azureClientId, '--output', 'json')
    if ($list.Code -ne 0) { throw "az federated-credential list failed: $($list.Out)" }
    $existing = @($list.Out | ConvertFrom-Json)
    if (-not ($existing | Where-Object { $_.subject -eq $subject })) {
        # Through a file (`@path`): az on Windows is a .cmd, and cmd strips the quotes of inline JSON.
        $name = "preview-$($t -replace '[^A-Za-z0-9-]', '-')"
        $file = New-TemporaryFile
        @{ name = $name; issuer = 'https://token.actions.githubusercontent.com'
           subject = $subject; audiences = @('api://AzureADTokenExchange') } | ConvertTo-Json -Compress | Set-Content $file -Encoding utf8NoBOM
        # A credential of this name with another subject (the repo's subject format changed) is updated in place.
        $verb = if ($existing | Where-Object { $_.name -eq $name }) { 'update' } else { 'create' }
        $target = @('--id', $c.azureClientId) + $(if ($verb -eq 'update') { @('--federated-credential-id', $name) } else { @() })
        try { $made = Invoke-Tool az (@('ad', 'app', 'federated-credential', $verb) + $target + @('--parameters', "@$file")) }
        finally { Remove-Item $file -ErrorAction SilentlyContinue }
        if ($made.Code -ne 0) { throw "az federated-credential $verb failed: $($made.Out)" }
    }
}

# Dispatches deploy.yml on the job branch and waits for that run; returns it. A run of $sha that is
# still going (its deploy's process died, J-019) is waited on instead of dispatching another. With
# -Resume (the board still says applying), the newest run of $sha counts even if it has finished:
# it finished after the process waiting on it died (J-023).
function Invoke-DeployRun($j, [string] $envName, [string] $branch = "factory/$($j.id)", [string] $sha = '', [switch] $Resume, [string] $Since = '') {
    $t = $j.target
    $runsPath = "/repos/$t/actions/runs?branch=$([uri]::EscapeDataString($branch))&event=workflow_dispatch"
    $runs = @((Invoke-GitHub GET $runsPath).Body.workflow_runs | Where-Object { $_ })
    $run = if ($sha) { $runs | Where-Object { ($Resume -or $_.status -ne 'completed') -and $_.head_sha -eq $sha } | Sort-Object { [long]$_.id } -Descending | Select-Object -First 1 }
    if (-not $run) {
        $before = $runs | ForEach-Object { [long]$_.id } | Measure-Object -Maximum
        $last = if ($before.Count) { [long]$before.Maximum } else { 0 }
        $inputs = @{ environment = $envName }
        if ($Since) { $inputs.since = $Since }
        $r = Invoke-GitHub POST "/repos/$t/actions/workflows/deploy.yml/dispatches" @{ ref = $branch; inputs = $inputs }
        if ($r.Status -ne 204) { throw "cannot dispatch deploy.yml: $(GhError $r)" }
    }
    $deadline = Deploy-Deadline
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
function Invoke-Deploy($j, [switch] $Resume, [string] $Since = '') {
    $d = $j.deploy
    $result = [ordered]@{ ok = $false; sha = $null; run = $null; urls = $null; destroyAt = $null; workspaceId = $null; log = '' }
    $wt = WorkDir $j
    $branch = "factory/$($j.id)"
    $push = Invoke-GitAuth $wt push -q -f origin "$($branch):$branch"
    if ($push.Code -ne 0) { $result.log = "## Push`nError: push failed: $($push.Out)"; return $result }
    $result.sha = (Invoke-Git $wt rev-parse HEAD).Out

    Exit-BoardLock
    try {
        $wsId = $null
        try {
            # GitHub first: a missing PAT permission fails here, before anything exists in HCP.
            Prepare-GitHub $j
            $project = Ensure-HcpProject (Repo-Name $j)
            foreach ($idle in 'dev', 'staging') { Ensure-HcpWorkspace "$(Repo-Name $j)-$idle" $project | Out-Null }
            $result.destroyAt = (Get-Date).ToUniversalTime().AddHours(24).ToString('yyyy-MM-ddTHH:mm:ssZ')
            $wsId = Ensure-HcpWorkspace $d.workspace $project $result.destroyAt
            $result.workspaceId = $wsId
            # since: the head the live preview was applied at, so a front-end-only change can skip
            # Terraform. Only when the branch's workflow declares it; GitHub refuses unknown inputs.
            $wf = Join-Path $wt '.github/workflows/deploy.yml'
            $since = if ($Since -and (Test-Path $wf) -and ((Get-Content $wf -Raw) -match '(?m)^\s+since:')) { $Since } else { '' }
            $run = Invoke-DeployRun $j $d.env -sha $result.sha -Resume:$Resume -Since $since
            $result.run = $run.html_url
            if ($run.conclusion -eq 'success') {
                $outs = Get-HcpOutputs $wsId @('qa_password')
                $result.urls = Preview-Urls $outs
                $signin = [Collections.Generic.List[string]]::new()
                $shots = Join-Path (JobDir $j.id) 'signin'
                $err = Invoke-SigninSmoke $outs $result.urls $shots $signin
                # A pass leaves evidence too (J-024's first live smoke left none).
                if ($signin.Count) { Set-Content (Join-Path $shots "smoke-$($j.deploy.attempts).log") $signin }
                if ($err) { $result.log = "## Sign-in smoke`nError: $err`n$($signin -join "`n")" } else { $result.ok = $true }
            } else {
                $result.log = "## GitHub`n$(Get-RunLog $j $run)`n`n## HCP`n$(Get-HcpRunLog $wsId)"
            }
        } catch {
            $hcp = if ($wsId) { try { "`n`n## HCP`n$(Get-HcpRunLog $wsId)" } catch { '' } } else { '' }
            $result.log = "## Factory`nError: $($_.Exception.Message)$hcp"
        }
    } finally {
        Enter-BoardLock -1
        $script:Board = Load-Board
    }
    $result
}

function Smoke-Seconds { if ($env:FACTORY_SMOKE_SECONDS) { [int]$env:FACTORY_SMOKE_SECONDS } else { 300 } }
function Signin-Seconds { if ($env:FACTORY_SIGNIN_SECONDS) { [int]$env:FACTORY_SIGNIN_SECONDS } else { 120 } }

# Deploy step 7a: signs in on the front end as the preview's QA user (signin-smoke.mjs, or
# FACTORY_SIGNIN_CMD in tests), the password on stdin only. Runs only when the outputs have both QA
# values and there is a front end. Returns $null (passed, or nothing to sign in as) or the failure;
# $lines gets the smoke's steps, never the password.
function Invoke-SigninSmoke($outs, $urls, [string] $shots, [Collections.Generic.List[string]] $lines) {
    $user, $pw = $outs['qa_user'], $outs['qa_password']
    if (-not $user -or -not $pw -or -not $urls -or -not $urls.frontend) { return $null }
    New-Item -ItemType Directory -Force $shots | Out-Null
    $a = @('--url', $urls.frontend, '--user', $user, '--shots', $shots, '--seconds', "$(Signin-Seconds)")
    $out = if ($env:FACTORY_SIGNIN_CMD) { $pw | & pwsh -NoProfile -File $env:FACTORY_SIGNIN_CMD @a 2>&1 }
           else { $pw | & node (Join-Path $PSScriptRoot 'signin-smoke.mjs') @a 2>&1 }
    $code = $LASTEXITCODE
    foreach ($l in @($out | ForEach-Object { "$_".Replace($pw, '***') })) { $lines.Add($l) }
    if ($code -eq 0) { return $null }
    $why = $lines | Where-Object { $_ -match '^error: ' } | Select-Object -First 1
    "sign-in smoke: $(if ($why) { $why -replace '^error: ', '' } else { "exited $code" })"
}

# GETs <api>/health and the front end until each answers 200, retrying every 10 s while a fresh
# container starts. Adds one line per url to $lines; returns $null, or the first failure.
# React's Static Web App links the API as its backend, which puts the API's own address behind the
# Static Web App's auth (it answers 401), so /health is reached the way a browser reaches it.
function Preview-Urls($outs) {
    $react, $api = $outs['react_url'], $outs['api_url']
    $health = if ($react) { "$($react.TrimEnd('/'))/api/health" } elseif ($api) { "$($api.TrimEnd('/'))/health" }
    [ordered]@{ frontend = $react ?? $outs['blazor_web_url']; api = $api; health = $health }
}

function Invoke-Smoke($urls, [Collections.Generic.List[string]] $lines) {
    if (-not $urls -or -not $urls.health) { $lines.Add('no api_url output'); return 'smoke: the workspace has no api_url output' }
    $deadline = (Get-Date).AddSeconds((Smoke-Seconds))
    $first = $null
    foreach ($u in @($urls.health) + @($urls.frontend | Where-Object { $_ })) {
        while ($true) {
            $code = 0; $body = ''; $server = '-'
            $sw = [Diagnostics.Stopwatch]::StartNew()
            try {
                $r = Invoke-WebRequest $u -TimeoutSec 30 -SkipHttpErrorCheck; $code = [int]$r.StatusCode; $body = "$($r.Content)"
                if ($r.Headers['Server']) { $server = "$($r.Headers['Server'])" }
            } catch { $body = $_.Exception.Message }
            if ($code -eq 200 -or (Get-Date) -ge $deadline) { break }
            Start-Sleep -Seconds 10
        }
        # The Server header tells the Container Apps ingress (envoy) from the app (Kestrel).
        $lines.Add("$u answered $code in $([int]$sw.Elapsed.TotalSeconds)s, server: $server $($body.Substring(0, [Math]::Min(200, $body.Length)))")
        if ($code -ne 200 -and -not $first) { $first = "smoke: $u answered $code" }
    }
    $first
}

# The first line that reads like an error, for the event and the flag.
# GitHub prints each step's script in cyan (ESC[36;1m) before running it, so an `echo "::error::..."`
# there is source, not an error. An action's `##[error]` or Terraform's `Error:` is the real thing;
# the closing "Process completed with exit code" says only that something failed.
function First-Error([string] $log) {
    $lines = @($log -split "`n" | Where-Object { $_ -notmatch '\x1b\[36;1m' } | ForEach-Object {
            ($_ -replace '\x1b\[[0-9;]*m', '' -replace '^\d{4}-\d\d-\d\dT[\d:.]+Z\s?', '' -replace '^[│╷╵\s]+', '').Trim()
        })
    $generic = '^##\[error\]Process completed with exit code'
    $line = $lines | Where-Object { ($_ -match '^##\[error\]' -and $_ -notmatch $generic) -or $_ -match '^Error: ' } | Select-Object -First 1
    if (-not $line) { $line = $lines | Where-Object { $_ -match '(?i)\berror\b' -and $_ -notmatch $generic } | Select-Object -First 1 }
    if (-not $line) { $line = $lines | Where-Object { $_ } | Select-Object -Last 1 }
    "$line" -replace '^##\[error\]', ''
}

# Starts `factory.ps1 destroy <id>` detached when the job has a preview that is not already gone.
function Start-Destroy($j) {
    if (-not $j.Contains('deploy') -or -not $j.deploy -or $j.deploy.status -in 'down', 'destroying', $null) { return }
    $a = '-NoProfile', '-File', (Join-Path $PSScriptRoot 'factory.ps1'), 'destroy', $j.id
    if ($IsWindows) { Start-Process pwsh -ArgumentList $a -WindowStyle Hidden | Out-Null } else { Start-Process pwsh -ArgumentList $a | Out-Null }
}

# A destroy run that did not apply, with Terraform's own first error from its log when there is one
# (canary-8's said only "errored"; the reason was a resource group Azure had added to).
function Destroy-Failure([string] $wsId, [string] $status) {
    $why = try { First-Error (Get-HcpRunLog $wsId) } catch { $null }
    "destroy run ended $status$(if ($why) { ": $why" })"
}

# Destroys the preview's resources and its workspace, lock released. Returns $null or an error.
function Invoke-Destroy($j) {
    $name = $j.deploy.workspace
    $known = if ($j.deploy.Contains('workspaceId')) { $j.deploy.workspaceId }
    Exit-BoardLock
    try {
        $wsId = if ($known) { $known } else { Get-HcpWorkspaceId $name }
        if ($wsId) {
            $status = Invoke-HcpDestroy $wsId (Poll-Seconds) (Deploy-Deadline)
            if ($status -ne 'applied') { return Destroy-Failure $wsId $status }
            Remove-HcpWorkspace $wsId
        }
        $null
    } catch {
        $_.Exception.Message
    } finally {
        Enter-BoardLock -1
        $script:Board = Load-Board
    }
}
