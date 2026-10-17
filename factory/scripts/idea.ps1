<#
.SYNOPSIS
  Opens an interactive Claude Code session on /factory:idea, with the factory plugin loaded, in this
  repo. See "Ideas" in docs/FACTORY-CONTRACT.md.
.EXAMPLE
  pwsh factory/scripts/idea.ps1 D:\code\Scratchpad\todo-app    # a new idea for that target
  pwsh factory/scripts/idea.ps1 I-007                          # resume a drafting idea
  pwsh factory/scripts/idea.ps1                                # the session asks for the target
#>
param([string] $Arg)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# The spec-writer reads the target; for a resumed idea, its hook events go to that idea.
$target = $null
if ($Arg -match '^I-\d+$') {
    $env:FACTORY_IDEA = $Arg
    $idea = (& (Join-Path $PSScriptRoot 'factory.ps1') status -Json | ConvertFrom-Json).ideas | Where-Object id -eq $Arg
    if ($idea) { $target = $idea.target }
} elseif ($Arg) {
    $target = $Arg
}
$dirs = @(if ($target -and (Test-Path $target -PathType Container)) { '--add-dir'; $target })

$prompt = "/factory:idea $Arg".Trim()
Push-Location $repo
try {
    & claude --plugin-dir (Join-Path $repo 'factory/plugin') @dirs $prompt
} finally {
    Pop-Location
}
