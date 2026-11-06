# The deploy canary for factory.ps1, which dot-sources this file after deploy.ps1. See "Canary" in
# docs/FACTORY-CONTRACT.md: a fresh scaffold of the current templates, deployed end to end as j000
# and torn down, so a broken deploy path costs a canary rather than a real job's attempts.

function CanaryPath { Join-Path $Home_ 'canary.json' }

function Get-Canary {
    $p = CanaryPath
    if (Test-Path $p) { Get-Content $p -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
}

function Save-Canary($c) {
    $tmp = "$(CanaryPath).$PID.tmp"
    [IO.File]::WriteAllText($tmp, ($c | ConvertTo-Json -Depth 10))
    Replace-File $tmp (CanaryPath)
}

function Canary-Repo {
    $v = Read-EnvFile (EnvFilePath)
    if ($v['FACTORY_CANARY_REPO']) { return $v['FACTORY_CANARY_REPO'] }
    if (-not $v['GITHUB_OWNER']) { Fail 'canary refused: set FACTORY_CANARY_REPO (or GITHUB_OWNER) in .env' }
    "$($v['GITHUB_OWNER'])/factory-canary"
}

function Canary-Options {
    $v = Read-EnvFile (EnvFilePath)
    $sc = if ($v['FACTORY_CANARY_SCAFFOLD']) { $v['FACTORY_CANARY_SCAFFOLD'] | ConvertFrom-Json -AsHashtable } else {
        # auth: every canary proves deploy step 7a, the sign-in smoke, end to end.
        [ordered]@{ name = 'canary'; resource = 'Item'; db = 'postgres'; ui = 'react'; events = $false; auth = $true }
    }
    $err = Test-Scaffold $sc
    if ($err) { Fail "canary refused: FACTORY_CANARY_SCAFFOLD: $err" }
    # Sign-in needs the Entra External ID ids in the scaffold's tfvars (a target commits them; the
    # canary is a fresh scaffold every run, canary 15). setup-azure.ps1 prints them.
    if ($sc.Contains('auth') -and $sc.auth) {
        $ids = [ordered]@{ entra_external_tenant_id = $v['FACTORY_ENTRA_TENANT_ID']; entra_external_subdomain = $v['FACTORY_ENTRA_SUBDOMAIN']
            entra_external_user_flow_id = $v['FACTORY_ENTRA_USER_FLOW_ID'] }
        if (@($ids.Values | Where-Object { -not $_ }).Count) {
            Fail 'canary refused: the canary scaffold has auth, and .env lacks FACTORY_ENTRA_TENANT_ID, FACTORY_ENTRA_SUBDOMAIN or FACTORY_ENTRA_USER_FLOW_ID (setup-azure.ps1 prints them)'
        }
        $sc.entra = $ids
    }
    $sc
}

# A SHA-256 over what the deploy path depends on: the factory's deploy scripts, the templates' IaC,
# deploy.yml and pack, the HCP variable set (sensitive values excepted) and the canary's own settings.
function Canary-Fingerprint {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($f in 'deploy.ps1', 'hcp.ps1', 'github.ps1') { $lines.Add("factory/$f $((Get-FileHash (Join-Path $PSScriptRoot $f)).Hash)") }
    $t = TemplatesRoot
    $files = @(Get-ChildItem (Join-Path $t 'IaC') -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '[\\/]\.terraform[\\/]' })
    $files += @(Get-Item (Join-Path $t 'base-project/.github/workflows/deploy.yml') -ErrorAction SilentlyContinue)
    $files += @(Get-ChildItem (Join-Path $t 'artifacts/nupkg') -Filter '*.nupkg' -File -ErrorAction SilentlyContinue)
    foreach ($f in $files | Sort-Object FullName) {
        $lines.Add("$([IO.Path]::GetRelativePath($t, $f.FullName) -replace '\\', '/') $((Get-FileHash $f.FullName).Hash)")
    }
    foreach ($v in Get-HcpVarsetVars | Sort-Object { $_.category }, { $_.key }) {
        $lines.Add("var $($v.category) $($v.key) $(if ($v.sensitive) { '<sensitive>' } else { $v.value })")
    }
    $lines.Add("canary $(Canary-Repo) $((Canary-Options) | ConvertTo-Json -Compress)")
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lines -join "`n"))
    'sha256:' + [Convert]::ToHexString($hash).ToLowerInvariant()
}

# 'pass' when canary.json records a pass for the current fingerprint, 'failed' when it records a
# failure for it, else 'none' (never run, or run for other inputs).
function Canary-State {
    $c = Get-Canary
    if (-not $c -or $c.fingerprint -ne (Canary-Fingerprint)) { return 'none' }
    if ($c.status -eq 'pass') { 'pass' } elseif ($c.status -eq 'failed') { 'failed' } else { 'none' }
}

# canary.lock holds the runner's PID; one left by a process that is gone is taken over.
function Lock-Canary {
    $p = Join-Path $Home_ 'canary.lock'
    if (Test-Path $p) {
        $old = "$(Get-Content $p -Raw)".Trim()
        if ($old -match '^\d+$' -and (Get-Process -Id ([int]$old) -ErrorAction SilentlyContinue)) { return $false }
        Remove-Item $p -Force
    }
    try {
        $fs = [IO.File]::Open($p, 'CreateNew', 'Write')
        $b = [Text.Encoding]::ASCII.GetBytes("$PID")
        $fs.Write($b, 0, $b.Length); $fs.Dispose()
        $true
    } catch { $false }
}

# Runs one command in $dir; returns { Code, Out }.
function Invoke-In([string] $dir, [string[]] $pwshArgs) {
    Push-Location $dir
    try { $out = & pwsh -NoProfile @pwshArgs 2>&1; [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($out | Out-String).Trim() } }
    finally { Pop-Location }
}

# Scaffolds into $dir and force-pushes it to the canary repo's main. Returns $null or an error.
function Publish-CanaryScaffold([string] $repo, $sc, [string] $dir, $rec, $log, [bool] $full) {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dir | Out-Null
    $cmds = [Collections.Generic.List[string[]]]::new()
    # The stub (tests) is handed the commands it stands in for, as arguments.
    if ($env:FACTORY_CANARY_SCAFFOLD_CMD) { $cmds.Add(@('-File', $env:FACTORY_CANARY_SCAFFOLD_CMD) + @(Scaffold-Commands $sc)) }
    else { foreach ($c in Scaffold-Commands $sc) { $cmds.Add(@('-Command', $c)) } }
    foreach ($c in $cmds) {
        $r = Invoke-In $dir $c
        if ($r.Code -ne 0) { $log.Add("## Scaffold`n$($c -join ' ')`n$($r.Out)"); return "scaffold: $($c[-1]) exited $($r.Code)" }
    }
    if ($sc.Contains('entra')) {
        $tfvars = Join-Path $dir 'IaC/envs/ephemeral.tfvars'
        if (-not (Test-Path $tfvars)) { return 'scaffold: no IaC/envs/ephemeral.tfvars to put the Entra ids in' }
        $lines = @(Get-Content $tfvars | Where-Object { $_ -notmatch '^\s*entra_external_(tenant_id|subdomain|user_flow_id)\s*=' })
        $w = ($sc.entra.Keys | Measure-Object -Property Length -Maximum).Maximum
        Set-Content $tfvars ($lines + @($sc.entra.Keys | ForEach-Object { "$($_.PadRight($w)) = `"$($sc.entra[$_])`"" }))
    }
    # The whole stack, off the shared platform: the only thing that proves a from-scratch create.
    if ($full) {
        $tfvars = Join-Path $dir 'IaC/envs/ephemeral.tfvars'
        if (-not (Test-Path $tfvars)) { return 'scaffold: no IaC/envs/ephemeral.tfvars to set use_platform in' }
        Add-Content $tfvars 'use_platform = false'
    }
    Invoke-Git $dir init -q -b main | Out-Null
    Invoke-Git $dir add -A | Out-Null
    $commit = Invoke-Git $dir -c user.name=factory -c user.email=factory@localhost commit -q -m 'canary scaffold'
    if ($commit.Code -ne 0) { return "scaffold: commit failed: $($commit.Out)" }
    $rec.sha = (Invoke-Git $dir rev-parse HEAD).Out

    $state = Get-TargetState $repo
    if ($state -like 'error: *') { return "github: $($state.Substring(7))" }
    if ($state -eq 'missing') { $err = New-GitHubRepo $repo; if ($err) { return "github: $err" } }
    $push = Invoke-GitAuth $dir push -q -f (CloneUrl $repo) HEAD:main
    if ($push.Code -ne 0) { return "github: push failed: $($push.Out)" }
    $null
}

# The run, lock released: scaffold, deploy j000 from main, smoke. Returns $null or the first error;
# $log collects the sections of canary-<n>.log. The teardown comes after the result (Invoke-CanaryTeardown).
function Invoke-CanaryRun([string] $repo, $sc, $rec, $log, [bool] $full) {
    $dir = Join-Path (WorkRoot) 'canary'
    $name = ($repo -split '/', 2)[1]
    $err = $null
    $wsId = $null
    try {
        $err = Publish-CanaryScaffold $repo $sc $dir $rec $log $full
        if (-not $err) {
            try {
                $cj = [ordered]@{ id = 'canary'; target = $repo }
                Prepare-GitHub $cj
                $project = Ensure-HcpProject $name
                $wsId = Ensure-HcpWorkspace "$name-j000" $project (Get-Date).ToUniversalTime().AddHours(24).ToString('yyyy-MM-ddTHH:mm:ssZ')
                $run = Invoke-DeployRun $cj 'j000' 'main'
                if ($run.conclusion -ne 'success') {
                    $log.Add("## GitHub`n$(Get-RunLog $cj $run)`n`n## HCP`n$(Get-HcpRunLog $wsId)")
                    $err = First-Error ($log -join "`n")
                } else {
                    $outs = Get-HcpOutputs $wsId @('qa_password')
                    $rec.urls = Preview-Urls $outs
                    $smoke = [Collections.Generic.List[string]]::new()
                    $err = Invoke-Smoke $rec.urls $smoke
                    $log.Add("## Smoke`n$($smoke -join "`n")")
                    # Step 7a, when the canary scaffold has sign-in (a QA user on j000).
                    if (-not $err) {
                        $signin = [Collections.Generic.List[string]]::new()
                        $err = Invoke-SigninSmoke $outs $rec.urls (Join-Path $Home_ 'canary-signin') $signin
                        if ($signin.Count) { $log.Add("## Sign-in smoke`n$(if ($err) { "Error: $err`n" })$($signin -join "`n")") }
                    }
                }
            } catch {
                $hcp = if ($wsId) { try { "`n`n## HCP`n$(Get-HcpRunLog $wsId)" } catch { '' } } else { '' }
                $log.Add("## Factory`nError: $($_.Exception.Message)$hcp")
                $err = First-Error ($log -join "`n")
            }
        }
    } finally {
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $err
}

# Holds a new canary until the last one's teardown lets go of j000. Called with the board lock; it
# gives that up while waiting, because the teardown needs it to record its result.
function Wait-CanaryTeardown {
    while ($true) {
        $c = Get-Canary
        $t = if ($c) { $c['teardown'] }
        if (-not $t -or $t.status -ne 'destroying' -or -not (Destroy-Running $t.startedAt)) { return }
        Exit-BoardLock
        try { Start-Sleep -Seconds ([Math]::Max(1, (Poll-Seconds))) } finally { Enter-BoardLock -1 }
    }
}

# factory.ps1 canary-destroy: j000 down, pass or fail, and the outcome under `teardown` in canary.json.
# A teardown that fails does not fail the canary; sweep retries it.
function Invoke-CanaryTeardown {
    $until = (Get-Date).AddSeconds(30)
    while (-not (Lock-Canary)) {
        if ((Get-Date) -gt $until) { Fail 'canary-destroy refused: another canary is running (canary.lock)' }
        Start-Sleep -Seconds 1
    }
    $lease = $null; $keeper = $null
    try {
        if (State-IsAzure) {
            $lease = Enter-StateLease 'canary' 0
            if (-not $lease) { Fail 'canary-destroy refused: another canary is running on another machine (locks/canary)' }
            $keeper = Start-StateLeaseKeeper 'canary' $lease
        }
        $rec = Get-Canary
        if (-not $rec) { return }
        $t = if ($rec['teardown']) { $rec.teardown } else { [ordered]@{ status = 'destroying'; error = $null; startedAt = $null; failures = 0 } }
        $t.status = 'destroying'
        $t.startedAt = Now
        $rec.teardown = $t
        Save-Canary $rec

        $name = ($rec.repo -split '/', 2)[1]
        Exit-BoardLock
        try {
            $down = try {
                $wsId = Get-HcpWorkspaceId "$name-j000"
                $gone = if ($wsId) { Remove-Preview $wsId }
                if ($gone) { $gone } elseif ($wsId -and (Get-HcpWorkspaceId "$name-j000")) { "workspace $name-j000 is still there" }
            } catch { $_.Exception.Message }
        } finally { Enter-BoardLock -1 }

        $rec = Get-Canary
        $t = $rec.teardown
        $t.status = if ($down) { 'failed' } else { 'down' }
        $t.error = if ($down) { "$down" }
        $t.failures = if ($down) { [int]$t.failures + 1 } else { 0 }
        Save-Canary $rec
        if ($rec.log) {
            Add-Content (Join-Path $Home_ $rec.log) "`n`n## Destroy`n$(if ($down) { "Error: $down" } else { "workspace $name-j000 destroyed and deleted" })"
        }
        Add-Event ([ordered]@{ job = 'canary'; kind = 'destroy'; detail = $(if ($down) { "failed: $down" } else { 'down' }) })
        $t.status
    } finally {
        if ($lease) { Stop-StateLeaseKeeper $keeper; try { Exit-StateLease 'canary' $lease } catch { } }
        Remove-Item (Join-Path $Home_ 'canary.lock') -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Canary([bool] $force, [bool] $full = $false) {
    if (-not (Deploys-On)) { Fail 'canary refused: deploys are off (no HCP_ORG in .env)' }
    $fp = Canary-Fingerprint
    $last = Get-Canary
    if (-not $force -and -not $full -and $last -and $last.fingerprint -eq $fp -and $last.status -eq 'pass') { return 'canary: up to date' }
    Wait-CanaryTeardown
    $last = Get-Canary
    if (-not (Lock-Canary)) { Fail 'canary refused: another canary is running (canary.lock)' }
    # In azure, also the lease on locks/canary: one canary across machines (see "State backends").
    $canaryLease = $null; $canaryKeeper = $null
    if (State-IsAzure) {
        $canaryLease = Enter-StateLease 'canary' 0
        if (-not $canaryLease) {
            Remove-Item (Join-Path $Home_ 'canary.lock') -Force -ErrorAction SilentlyContinue
            Fail 'canary refused: another canary is running on another machine (locks/canary)'
        }
        $canaryKeeper = Start-StateLeaseKeeper 'canary' $canaryLease
    }
    try {
        $repo = Canary-Repo
        $sc = Canary-Options
        $n = 1 + $(if ($last) { [int]$last.n } else { 0 })
        $rec = [ordered]@{ fingerprint = $fp; status = 'running'; error = $null; repo = $repo; sha = $null; urls = $null
            log = "canary-$n.log"; startedAt = (Now); finishedAt = $null; n = $n; full = $full }
        Save-Canary $rec
        $log = [Collections.Generic.List[string]]::new()
        Exit-BoardLock
        try { $err = Invoke-CanaryRun $repo $sc $rec $log $full }
        finally { Enter-BoardLock -1 }

        Set-Content (Join-Path $Home_ $rec.log) ($log -join "`n`n")
        $rec.status = if ($err) { 'failed' } else { 'pass' }
        $rec.error = $err
        $rec.finishedAt = Now
        $rec.teardown = [ordered]@{ status = 'destroying'; error = $null; startedAt = (Now); failures = 0 }
        Save-Canary $rec
        Add-Event ([ordered]@{ kind = 'canary'; detail = $(if ($err) { "failed: $err" } else { 'pass' }) })
    } finally {
        if ($canaryLease) { Stop-StateLeaseKeeper $canaryKeeper; try { Exit-StateLease 'canary' $canaryLease } catch { } }
        Remove-Item (Join-Path $Home_ 'canary.lock') -Force -ErrorAction SilentlyContinue
    }
    # Pass or fail, j000 comes down, and nothing waits for it: a destroy takes as long as the deploy.
    Start-FactoryDetached @('canary-destroy')
    if ($err) { Fail "canary failed: $err; see $($rec.log)" }
    "canary: pass ($($rec.urls.api))"
}
