<#
.SYNOPSIS
  Runs `/factory:factory spec <ideaId>` headless in this repo with FACTORY_IDEA set, streaming to
  ideas/<id>/run.jsonl, and marks the idea failed if it is still drafting afterwards. The idea's
  target is added as a readable dir when it exists. Started by serve.ps1 on POST /api/ideas.
  $env:FACTORY_CLAUDE_CMD replaces the claude call in tests.
#>
param([Parameter(Mandatory)] [string] $IdeaId)

$ErrorActionPreference = 'Stop'
$Factory = Join-Path $PSScriptRoot 'factory.ps1'
function F { & $Factory @args }
. (Join-Path $PSScriptRoot 'claude-run.ps1')

$mainRoot = if ($env:FACTORY_REPO) { $env:FACTORY_REPO } else {
    Split-Path ((git rev-parse --path-format=absolute --git-common-dir) -replace '/', '\') -Parent
}
$env:FACTORY_REPO = $mainRoot
if (-not $env:FACTORY_HOME) { $env:FACTORY_HOME = Join-Path $mainRoot '.factory-runs' }
$env:FACTORY_IDEA = $IdeaId

$target = ((F status -Json | ConvertFrom-Json).ideas | Where-Object id -eq $IdeaId).target
$extra = @(if ($target -and (Test-Path $target)) { $target })

Push-Location $mainRoot
try {
    $state = $env:FACTORY_HOME.Replace('\', '/')
    $run = Invoke-ClaudeStream "/factory:factory spec $IdeaId $state $($Factory.Replace('\', '/'))" `
        (Join-Path $env:FACTORY_HOME "ideas/$IdeaId/run.jsonl") $extra
    $code = $run.Code
} finally {
    Pop-Location
}

$idea = (F status -Json | ConvertFrom-Json).ideas | Where-Object id -eq $IdeaId
if ($idea -and $idea.status -eq 'drafting') {
    F idea-done $IdeaId -Failed -Reason "spec run ended (exit $code) without closing the idea; see run.jsonl" | Out-Null
}
exit 0
