<#
.SYNOPSIS
  SubagentStart / SubagentStop hook. Reads the hook JSON from stdin and appends agent-start or
  agent-stop for $env:FACTORY_JOB (or $env:FACTORY_IDEA). The plugin's `factory:` prefix is
  stripped, and agent-start records the agent id and its live transcript path. Always exits 0 and
  prints nothing, so a hook can never break a normal session.
#>
try {
    if (-not $env:FACTORY_JOB -and -not $env:FACTORY_IDEA) { exit 0 }
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $agent = $payload.agent_type -replace '^factory:', ''
    if (-not $agent) { exit 0 }   # Claude Code's internal agents report an empty agent_type
    $kind = switch ($payload.hook_event_name) {
        'SubagentStart' { 'agent-start' }
        'SubagentStop' { 'agent-stop' }
        default { exit 0 }
    }
    $e = [ordered]@{}
    if ($env:FACTORY_JOB) { $e.job = $env:FACTORY_JOB } else { $e.idea = $env:FACTORY_IDEA }
    $e.kind = $kind
    $e.agent = $agent
    if ($kind -eq 'agent-start' -and $payload.agent_id -and $payload.transcript_path) {
        $e.agentId = $payload.agent_id
        $session = $payload.transcript_path -replace '\.jsonl$', ''
        $e.transcript = [IO.Path]::GetFullPath((Join-Path $session "subagents/agent-$($payload.agent_id).jsonl"))
    }
    & (Join-Path $PSScriptRoot 'factory.ps1') event ($e | ConvertTo-Json -Compress) *> $null
} catch { }
exit 0
