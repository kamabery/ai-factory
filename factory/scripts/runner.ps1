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
$Building = 'build', 'review', 'approve', 'gate'

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

# The runner runs in a hidden window; this log is the only place its failures can be seen.
$runLog = Join-Path $env:FACTORY_HOME 'runner.log'
function Log([string] $msg) { Add-Content -LiteralPath $runLog "$((Get-Date).ToUniversalTime().ToString('o')) $msg" -Encoding utf8 }

# 'stopped' unless the loop ends on its own: a closed console runs finally but not catch.
$how = 'stopped'
Log "start pid=$PID"
try {
    while ($true) {
        $id = F next
        if (-not $id) { $how = 'drained'; break }

        F set-stage $id build -By runner | Out-Null
        if ($LASTEXITCODE -ne 0) { $how = "fatal: set-stage $id build refused"; break }
        Log "take $id"

        # One job's failure must not stall the queue: log it, escalate the job, take the next.
        try {
            $j = Get-StatusJob $id
            $provider = if ($j.PSObject.Properties['provider'] -and $j.provider) { $j.provider } else { 'subscription' }
            $childEnv = Get-ProviderEnv $provider
            if ($null -eq $childEnv) {
                Log "provider $id failed: no OPENROUTER_API_KEY in .env"
                F set-stage $id ready -By runner -Reason escalated -Detail 'provider: no OPENROUTER_API_KEY in .env' | Out-Null
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
                $run = Invoke-ClaudeStream "/factory:factory run $id $state $($Factory.Replace('\', '/'))" (Join-Path $jobDir "run-$n.jsonl") -childEnv $childEnv
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
            }

            # The approver approved and only the gate is left; run it rather than escalate.
            if ((Stage $id) -eq 'gate') { F gate $id | Out-Null }

            if ((Stage $id) -in $Building) {
                $why = if ($code -ne 0) { "claude exited with exit $code" } else { 'claude finished but the job is still in Building' }
                F set-stage $id ready -By runner -Reason escalated -Detail "$why; see run-$n.jsonl" | Out-Null
            }

            # Push the branch and open the PR, so the human reviews it on GitHub. merge publishes again.
            if ((Stage $id) -eq 'ready') {
                $pub = F-Child publish $id
                if ($pub.Code -ne 0) { Log "publish $($id): $($pub.Err)" }
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
    Remove-Item $lock -Force -ErrorAction SilentlyContinue
}
exit 0
