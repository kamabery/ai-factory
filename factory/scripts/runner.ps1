<#
.SYNOPSIS
  Drains the Approved queue one job at a time, each in its own temp clone; see "Runner" in
  docs/FACTORY-CONTRACT.md. Started by serve.ps1 after an approve or rework; safe to start twice.
.DESCRIPTION
  Each pass runs in the job's work dir through claude-run.ps1 (plugin, headless settings,
  stream-json to run-<n>.jsonl). $env:FACTORY_CLAUDE_CMD, when set, replaces claude (it runs
  through pwsh -Command with FACTORY_JOB set), so tests spend no tokens.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$Factory = Join-Path $PSScriptRoot 'factory.ps1'
. (Join-Path $PSScriptRoot 'claude-run.ps1')
$Building = 'build', 'review', 'deploy', 'approve', 'gate'

function F { & $Factory @args }
# factory.ps1 in a child process, for a call whose refusal must be read: Fail writes to the console's
# stderr, which an in-process call cannot capture. Returns { Code, Out (stdout), Err (stderr) }.
function F-Child {
    $lines = & pwsh -NoProfile -File $Factory @args 2>&1
    $code = $LASTEXITCODE
    [pscustomobject]@{
        Code = $code
        Out = (@($lines | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }) -join "`n").Trim()
        Err = (@($lines | Where-Object { $_ -is [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join ' ').Trim()
    }
}

$mainRoot = if ($env:FACTORY_REPO) { $env:FACTORY_REPO } else {
    Split-Path ((git rev-parse --path-format=absolute --git-common-dir) -replace '/', '\') -Parent
}
$env:FACTORY_REPO = $mainRoot
if (-not $env:FACTORY_HOME) { $env:FACTORY_HOME = Join-Path $mainRoot '.factory-runs' }
New-Item -ItemType Directory -Force $env:FACTORY_HOME | Out-Null
$lock = Join-Path $env:FACTORY_HOME 'runner.lock'

# Strangler routing (contract, "Implementations"): with FACTORY_IMPL=dotnet the .NET runner drains the queue
# instead, on the same lock, log and state. It calls factory.ps1 as this script does.
if ($env:FACTORY_IMPL -eq 'dotnet') {
    $dll = if ($env:FACTORY_RUNNER) { $env:FACTORY_RUNNER } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/bin/Factory.Runner/debug/Factory.Runner.dll')) }
    if (-not (Test-Path $dll)) { [Console]::Error.WriteLine("runner: FACTORY_IMPL=dotnet, but the .NET runner is not built at $dll (dotnet build src/factory)"); exit 1 }
    $env:FACTORY_SCRIPTS = $PSScriptRoot
    # The contract's default run mode for a factory started from PowerShell; FACTORY_MODE in the environment or .env wins.
    if (-not $env:Factory__Mode) { $env:Factory__Mode = 'local' }
    & dotnet $dll
    exit $LASTEXITCODE
}

function Take-Lock {
    try {
        $fs = [IO.File]::Open($lock, 'CreateNew', 'Write', 'None')
    } catch [IO.IOException] {
        $holder = (Get-Content $lock -ErrorAction SilentlyContinue | Select-Object -First 1) -as [int]
        if ($holder -and (Get-Process -Id $holder -ErrorAction SilentlyContinue)) { return $false }
        Remove-Item $lock -Force -ErrorAction SilentlyContinue
        try { $fs = [IO.File]::Open($lock, 'CreateNew', 'Write', 'None') } catch [IO.IOException] { return $false }
    }
    $bytes = [Text.Encoding]::ASCII.GetBytes("$PID")
    $fs.Write($bytes, 0, $bytes.Length)
    $fs.Dispose()
    $true
}

function Get-StatusJob([string] $id) { (F status -Json | ConvertFrom-Json).jobs | Where-Object id -eq $id }
function Stage([string] $id) { (Get-StatusJob $id).stage }

if (-not (Take-Lock)) { exit 0 }

# In azure, runner.lock is also the lease on locks/runner, so one runner drains the queue across
# machines (see "State backends" in the contract). A lease held elsewhere is a running runner.
. (Join-Path $PSScriptRoot 'state.ps1')
Initialize-State -StateHome $env:FACTORY_HOME -EnvFile $(if ($env:FACTORY_ENV_FILE) { $env:FACTORY_ENV_FILE } else { Join-Path $mainRoot '.env' })
$lease = $null; $keeper = $null
if (State-IsAzure) {
    $lease = Enter-StateLease 'runner' 0
    if (-not $lease) { Remove-Item $lock -Force -ErrorAction SilentlyContinue; exit 0 }
    $keeper = Start-StateLeaseKeeper 'runner' $lease
}

# The runner runs in a hidden window; this log is the only place its failures can be seen.
$runLog = Join-Path $env:FACTORY_HOME 'runner.log'
function Log([string] $msg) { Add-Content -LiteralPath $runLog "$((Get-Date).ToUniversalTime().ToString('o')) $msg" -Encoding utf8 }

# 'stopped' unless the loop ends on its own: a closed console runs finally but not catch.
$how = 'stopped'
Log "start pid=$PID"
try {
    $canaryRan = $false
    while ($true) {
        # A deployable job waits for a canary pass on the current deploy inputs (see "Canary" in the
        # contract). One run per drain: a failure leaves those jobs in Approved for the human.
        $id = if ($canaryRan) { F next } else { F next -NeedsCanary }
        if ($id -eq 'canary') {
            $canaryRan = $true
            $c = F-Child canary
            Log $(if ($c.Code -eq 0) { 'canary pass' } else { "canary failed: $($c.Err -replace '^factory: canary failed: ', '')" })
            continue
        }
        if (-not $id) {
            $how = 'drained'
            # Previews that outlived their work; a failure here must not read as a failed drain.
            $sw = F-Child sweep
            if ($sw.Code -ne 0) { Log "sweep failed: $($sw.Err)" } elseif ($sw.Out) { Log "sweep started: $($sw.Out -replace "`r?`n", ' ')" }
            break
        }

        # A redeployed job resumes at deploy, and a resumed one at the stage a usage limit stopped it in,
        # continuing that claude session; everything else starts at build.
        $sj = Get-StatusJob $id
        $resume = $sj.PSObject.Properties['resume']
        $session = if ($sj.PSObject.Properties['resumeSession'] -and $sj.resumeSession) { "$($sj.resumeSession)" } else { '' }
        $to = if ($resume -and $resume.Value -in 'build', 'review', 'deploy', 'approve') { $resume.Value } else { 'build' }
        F set-stage $id $to -By runner | Out-Null
        if ($LASTEXITCODE -ne 0) { $how = "fatal: set-stage $id $to refused"; break }
        Log "take $id"

        # One job's failure must not stall the queue: log it, escalate the job, take the next.
        try {
            # The run mode decides the provider; a job's own provider field (older factories) is ignored.
            $childEnv = $null; $why = 'provider: no FACTORY_FOUNDRY_RESOURCE'
            try { $childEnv = Get-ProviderEnv $RunEnvFile } catch { $why = "provider: $($_.Exception.Message)" }
            if ($null -eq $childEnv) {
                Log "provider $id failed: $why"
                F set-stage $id ready -By runner -Reason escalated -Detail $why | Out-Null
                continue
            }

            $co = F-Child checkout $id
            if ($co.Code -ne 0) {
                Log "checkout $id failed: $($co.Err)"
                F set-stage $id ready -By runner -Reason escalated -Detail "work dir: $($co.Err)" | Out-Null
                continue
            }
            $wt = $co.Out

            $jobDir = Join-Path $env:FACTORY_HOME "jobs/$id"
            $n = @(Get-ChildItem $jobDir -Filter 'run-*.jsonl' -ErrorAction SilentlyContinue).Count + 1
            $env:FACTORY_JOB = $id

            Push-Location $wt
            try {
                $state = $env:FACTORY_HOME.Replace('\', '/')
                $runCmd = "/factory:factory run $id $state $($Factory.Replace('\', '/'))"
                $prompt = if ($session) {
                    "A usage limit stopped this run, and it has reset. Continue ``$runCmd`` from where you stopped: read the job's stage with ``status -Json`` first, and re-run the current step's agent if its file is missing."
                } else { $runCmd }
                $run = Invoke-ClaudeStream $prompt (Join-Path $jobDir "run-$n.jsonl") -childEnv $childEnv -resumeSession $session
                $code = $run.Code
                $result = $run.Result
            } finally {
                Pop-Location
                Remove-Item Env:FACTORY_JOB -ErrorAction SilentlyContinue
            }
            Log "claude $id exit=$code result=$(if ($result) { 'yes' } else { 'no' })"

            if ($result -and $result.PSObject.Properties['total_cost_usd']) {
                $ms = if ($result.PSObject.Properties['duration_ms']) { $result.duration_ms } else { 0 }
                F set-cost $id ([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $result.total_cost_usd)) $ms | Out-Null
                # A refused cost is an error finishing the job, on either implementation (a forwarded one exits, not throws).
                if ($LASTEXITCODE) { throw "set-cost $id refused (exit $LASTEXITCODE)" }
            }

            # A usage limit (the subscription's, or a Foundry 429) is no one's decision: flag it with the
            # session to continue, and stop, since the next job would hit the same limit. next holds the queue.
            $limited = $result -and (($result.PSObject.Properties['api_error'] -and $result.api_error -eq 'usage_limit_reached') -or
                ($result.PSObject.Properties['api_error_status'] -and "$($result.api_error_status)" -eq '429'))
            if ($limited -and (Stage $id) -in 'build', 'review', 'deploy', 'approve' -and $result.PSObject.Properties['session_id']) {
                $text = if ($result.PSObject.Properties['result']) { "$($result.result)" } else { 'usage limit' }
                F set-stage $id ready -By runner -Reason usage-limit -Detail $text -Session $result.session_id | Out-Null
                if ($LASTEXITCODE -eq 0) { $how = 'usage-limit'; break }
            }

            # The approver approved and only the gate is left; run it rather than escalate.
            if ((Stage $id) -eq 'gate') { F gate $id | Out-Null }

            if ((Stage $id) -in $Building) {
                $why = if ($code -ne 0) { "claude exited with exit $code" } else { 'claude finished but the job is still in Building' }
                if ($result -and $result.PSObject.Properties['is_error'] -and $result.is_error -and $result.PSObject.Properties['result']) { $why += ": $($result.result)" }
                F set-stage $id ready -By runner -Reason escalated -Detail "$why; see run-$n.jsonl" | Out-Null
            }

            # Push the branch and open the PR, so the human reviews it on GitHub. merge publishes again.
            # escalated lands a deployable job in blocked (see "Blocked" in the contract), which is published too.
            if ((Stage $id) -in 'ready', 'blocked') {
                $pub = F-Child publish $id
                if ($pub.Code -ne 0) { Log "publish $($id): $($pub.Err)" }
                # What a job that went bad taught, for the templates; a clean job writes nothing.
                $les = F-Child lesson $id
                if ($les.Code -ne 0) { Log "lesson $($id): $($les.Err)" }
            }
        } catch {
            Log "error $($id): $($_.Exception.Message)`n$($_.ScriptStackTrace)"
            if ((Stage $id) -in $Building) {
                F set-stage $id ready -By runner -Reason escalated -Detail "runner error: $($_.Exception.Message)" | Out-Null
            }
        } finally {
            Log "stage $id $(Stage $id)"
        }
    }
} catch {
    $how = "fatal: $($_.Exception.Message)"
    Log $_.ScriptStackTrace
} finally {
    Log "exit $how"
    if ($lease) { Stop-StateLeaseKeeper $keeper; try { Exit-StateLease 'runner' $lease } catch { } }
    Remove-Item $lock -Force -ErrorAction SilentlyContinue
}
exit 0
