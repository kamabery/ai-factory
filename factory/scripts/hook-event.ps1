<#
.SYNOPSIS
  SubagentStart / SubagentStop hook. Reads the hook JSON from stdin and appends agent-start or
  agent-stop for $env:FACTORY_JOB (or $env:FACTORY_IDEA). Always exits 0 and prints nothing, so a
  hook can never break a normal session.
#>
try {
    if (-not $env:FACTORY_JOB -and -not $env:FACTORY_IDEA) { exit 0 }
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $agent = $payload.agent_type
    if (-not $agent) { exit 0 }   # Claude Code's internal agents report an empty agent_type
    $kind = switch ($payload.hook_event_name) {
        'SubagentStart' { 'agent-start' }
        'SubagentStop' { 'agent-stop' }
        default { exit 0 }
    }
    $factory = Join-Path $PSScriptRoot 'factory.ps1'
    if ($env:FACTORY_JOB) {
        & $factory event -Job $env:FACTORY_JOB -Kind $kind -Agent $agent *> $null
    } else {
        & $factory event -Idea $env:FACTORY_IDEA -Kind $kind -Agent $agent *> $null
    }
} catch { }
exit 0
