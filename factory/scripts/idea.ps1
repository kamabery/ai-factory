<#
.SYNOPSIS
  Opens an interactive Claude Code session on /factory:idea, with the factory plugin loaded, in this
  repo. See "Ideas" in docs/FACTORY-CONTRACT.md.
.EXAMPLE
  pwsh factory/scripts/idea.ps1 kamabery/todo-app             # a new idea for that GitHub target
  pwsh factory/scripts/idea.ps1 I-007                         # resume a drafting idea
  pwsh factory/scripts/idea.ps1                               # the session asks for the target
#>
param([string] $Arg)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$factory = Join-Path $PSScriptRoot 'factory.ps1'

# The spec-writer reads the target; for a resumed idea, its hook events go to that idea.
$target = $null
if ($Arg -match '^I-\d+$') {
    $env:FACTORY_IDEA = $Arg
    $idea = (& $factory status -Json | ConvertFrom-Json).ideas | Where-Object id -eq $Arg
    if ($idea) { $target = $idea.target }
} elseif ($Arg) {
    $target = $Arg
}

# An existing target is read from a shallow clone in <work>\cli\<owner>-<name>, deleted on exit.
$clone = $null
if ($target -and (& $factory check-target $target 2>$null) -eq 'existing') {
    $work = if ($env:FACTORY_WORK) { $env:FACTORY_WORK } else { Join-Path ([IO.Path]::GetTempPath()) 'ai-factory' }
    $clone = & $factory clone-target $target (Join-Path $work "cli/$($target -replace '/', '-')")
    if ($LASTEXITCODE -ne 0) { $clone = $null }
}
$dirs = @(if ($clone) { '--add-dir'; $clone })

$prompt = "/factory:idea $Arg$(if ($clone) { " clone=$clone" })".Trim()
Push-Location $repo
try {
    & claude --plugin-dir (Join-Path $repo 'factory/plugin') @dirs $prompt
} finally {
    Pop-Location
    if ($clone) { Remove-Item -LiteralPath $clone -Recurse -Force -ErrorAction SilentlyContinue }
}
