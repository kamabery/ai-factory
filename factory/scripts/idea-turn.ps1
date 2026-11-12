<#
.SYNOPSIS
  Runs one assistant turn of a dashboard idea session. See "Idea sessions" in docs/FACTORY-CONTRACT.md.
.DESCRIPTION
  The dashboard starts it detached. The turn itself is the .NET CLI's `idea-turn` (roadmap spec 10), under every
  FACTORY_IMPL: one `claude -p` pass on the run mode's provider, with the idea tools as an MCP server. This script
  only hands it the state dir and the .env, and returns its exit code (2: no such session; 3: a turn is running).
#>
param(
    [Parameter(Mandatory)] [string] $Session,
    [string] $StateDir,
    [string] $EnvFile
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if ($StateDir) { $env:FACTORY_HOME = [IO.Path]::GetFullPath($StateDir) }
if ($EnvFile) { $env:FACTORY_ENV_FILE = $EnvFile }
$env:FACTORY_SCRIPTS = $PSScriptRoot
$dll = if ($env:FACTORY_CLI) { $env:FACTORY_CLI } else { Join-Path $Repo 'artifacts/bin/Factory.Cli/debug/Factory.Cli.dll' }
if (-not (Test-Path $dll)) { [Console]::Error.WriteLine("idea-turn: the .NET CLI is not built at $dll (dotnet build src/factory)"); exit 1 }
& dotnet $dll idea-turn $Session
exit $LASTEXITCODE
