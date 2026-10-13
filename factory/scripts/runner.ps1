<#
.SYNOPSIS
  Drains the Approved queue one job at a time in the factory worktree; see "Runner" in
  docs/FACTORY-CONTRACT.md. Started by serve.ps1 after an approve or rework; safe to start twice.
.DESCRIPTION
  $env:FACTORY_CLAUDE_CMD, when set, replaces `claude -p "/factory run <id>"` (it runs through
  pwsh -Command with FACTORY_JOB set), so tests spend no tokens.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$Factory = Join-Path $PSScriptRoot 'factory.ps1'
# Headless runs: edits are auto-accepted in the worktree and the state dir; Bash goes through the
# allowlist in .claude/settings.json; no factory job may edit the factory itself.
$fence = @('Edit(/factory/**)', 'Edit(/.claude/**)', 'Edit(/docs/FACTORY-CONTRACT.md)')
$Building = 'build', 'review', 'approve', 'gate'

function F { & $Factory @args }

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

function Stage([string] $id) {
    ((F status -Json | ConvertFrom-Json).jobs | Where-Object id -eq $id).stage
}

if (-not (Take-Lock)) { exit 0 }

try {
    while ($true) {
        $id = F next
        if (-not $id) { break }

        F set-stage $id build -By runner | Out-Null
        if ($LASTEXITCODE -ne 0) { break }

        $wt = F checkout $id 2>&1
        if ($LASTEXITCODE -ne 0) {
            F set-stage $id ready -By runner -Reason escalated -Detail "worktree: $wt" | Out-Null
            continue
        }

        $jobDir = Join-Path $env:FACTORY_HOME "jobs/$id"
        $n = @(Get-ChildItem $jobDir -Filter 'run-*.json' -ErrorAction SilentlyContinue).Count + 1
        $env:FACTORY_JOB = $id

        Push-Location $wt
        try {
            $out = if ($env:FACTORY_CLAUDE_CMD) {
                & pwsh -NoProfile -Command $env:FACTORY_CLAUDE_CMD
            } else {
                & claude -p "/factory run $id $($env:FACTORY_HOME.Replace('\', '/'))" --output-format json --permission-mode acceptEdits `
                    --add-dir $env:FACTORY_HOME --disallowedTools @fence
            }
            $code = $LASTEXITCODE
        } finally {
            Pop-Location
            Remove-Item Env:FACTORY_JOB -ErrorAction SilentlyContinue
        }

        $text = ($out | Out-String).Trim()
        Set-Content (Join-Path $jobDir "run-$n.json") $text
        $result = $null
        try { $result = ($text -split "`n" | Select-Object -Last 1) | ConvertFrom-Json } catch { }
        if ($result -and $result.PSObject.Properties['total_cost_usd']) {
            $ms = if ($result.PSObject.Properties['duration_ms']) { $result.duration_ms } else { 0 }
            F set-cost $id ([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $result.total_cost_usd)) $ms | Out-Null
        }

        if ((Stage $id) -in $Building) {
            $why = if ($code -ne 0) { "claude exited with exit $code" } else { 'claude finished but the job is still in Building' }
            F set-stage $id ready -By runner -Reason escalated -Detail "$why; see run-$n.json" | Out-Null
        }
    }
}
finally {
    Remove-Item $lock -Force -ErrorAction SilentlyContinue
}
exit 0
