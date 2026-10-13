<#
.SYNOPSIS
  Runs `claude -p "/factory spec <ideaId>"` in the main checkout with FACTORY_IDEA set, saves the
  result to ideas/<id>/run.json, and marks the idea failed if it is still drafting afterwards.
  Started by serve.ps1 on POST /api/ideas. $env:FACTORY_CLAUDE_CMD replaces the claude call in tests.
#>
param([Parameter(Mandatory)] [string] $IdeaId)

$ErrorActionPreference = 'Stop'
$Factory = Join-Path $PSScriptRoot 'factory.ps1'
function F { & $Factory @args }

$mainRoot = if ($env:FACTORY_REPO) { $env:FACTORY_REPO } else {
    Split-Path ((git rev-parse --path-format=absolute --git-common-dir) -replace '/', '\') -Parent
}
$env:FACTORY_REPO = $mainRoot
if (-not $env:FACTORY_HOME) { $env:FACTORY_HOME = Join-Path $mainRoot '.factory-runs' }
$env:FACTORY_IDEA = $IdeaId

Push-Location $mainRoot
try {
    $out = if ($env:FACTORY_CLAUDE_CMD) {
        & pwsh -NoProfile -Command $env:FACTORY_CLAUDE_CMD
    } else {
        & claude -p "/factory spec $IdeaId" --output-format json
    }
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}

$dir = Join-Path $env:FACTORY_HOME "ideas/$IdeaId"
New-Item -ItemType Directory -Force $dir | Out-Null
Set-Content (Join-Path $dir 'run.json') (($out | Out-String).Trim())

$idea = (F status -Json | ConvertFrom-Json).ideas | Where-Object id -eq $IdeaId
if ($idea -and $idea.status -eq 'drafting') {
    F idea-done $IdeaId -Failed -Reason "spec run ended (exit $code) without closing the idea; see run.json" | Out-Null
}
exit 0
