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
function Publish-CanaryScaffold([string] $repo, $sc, [string] $dir, $rec, $log) {
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

# The whole run, lock released: scaffold, deploy j000 from main, smoke, destroy. Returns $null or
# the first error; $log collects the sections of canary-<n>.log.
function Invoke-CanaryRun([string] $repo, $sc, $rec, $log) {
    $dir = Join-Path (WorkRoot) 'canary'
    $name = ($repo -split '/', 2)[1]
    $err = $null
    $wsId = $null
    try {
        $err = Publish-CanaryScaffold $repo $sc $dir $rec $log
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
        # Pass or fail: a canary is never left standing, and a teardown that fails fails the canary.
        if (-not $wsId) { $wsId = try { Get-HcpWorkspaceId "$name-j000" } catch { $null } }
        if ($wsId) {
            $down = try {
                $status = Invoke-HcpDestroy $wsId (Poll-Seconds) (Deploy-Deadline)
                if ($status -ne 'applied') { Destroy-Failure $wsId $status }
                else { Remove-HcpWorkspace $wsId; if (Get-HcpWorkspaceId "$name-j000") { "workspace $name-j000 is still there" } }
            } catch { $_.Exception.Message }
            $log.Add("## Destroy`n$(if ($down) { "Error: $down" } else { "workspace $name-j000 destroyed and deleted" })")
            if ($down -and -not $err) { $err = "destroy: $down" }
        }
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $err
}

function Invoke-Canary([bool] $force) {
    if (-not (Deploys-On)) { Fail 'canary refused: deploys are off (no HCP_ORG in .env)' }
    $fp = Canary-Fingerprint
    $last = Get-Canary
    if (-not $force -and $last -and $last.fingerprint -eq $fp -and $last.status -eq 'pass') { return 'canary: up to date' }
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
            log = "canary-$n.log"; startedAt = (Now); finishedAt = $null; n = $n }
        Save-Canary $rec
        $log = [Collections.Generic.List[string]]::new()
        Exit-BoardLock
        try { $err = Invoke-CanaryRun $repo $sc $rec $log }
        finally { Enter-BoardLock -1 }

        Set-Content (Join-Path $Home_ $rec.log) ($log -join "`n`n")
        $rec.status = if ($err) { 'failed' } else { 'pass' }
        $rec.error = $err
        $rec.finishedAt = Now
        Save-Canary $rec
        Add-Event ([ordered]@{ kind = 'canary'; detail = $(if ($err) { "failed: $err" } else { 'pass' }) })
    } finally {
        if ($canaryLease) { Stop-StateLeaseKeeper $canaryKeeper; try { Exit-StateLease 'canary' $canaryLease } catch { } }
        Remove-Item (Join-Path $Home_ 'canary.lock') -Force -ErrorAction SilentlyContinue
    }
    if ($err) { Fail "canary failed: $err; see $($rec.log)" }
    "canary: pass ($($rec.urls.api))"
}
