<#
.SYNOPSIS
  The factory control plane. Every state change goes through here; see docs/FACTORY-CONTRACT.md.
.DESCRIPTION
  factory.ps1 <command> [args]. A refused command exits non-zero with a one-line reason on stderr
  and leaves the board unchanged. read-reviews and read-decision exit 2 when a verdict is missing.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string] $Command = 'status',
    [Parameter(Position = 1)] [string] $Arg1,
    [Parameter(Position = 2)] [string] $Arg2,
    [Parameter(Position = 3)] [string] $Arg3,
    [string] $By,
    [string] $Reason,
    [string] $Detail,
    [string] $File,
    [string] $Job,
    [string] $Idea,
    [string] $Kind,
    [string] $Agent,
    [string] $Target,
    [string] $Scaffold,
    [string] $Base,
    [switch] $Failed,
    [switch] $Json,
    [switch] $Force,
    [switch] $Full,
    [switch] $NeedsCanary,
    [string] $Session
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$Roles = 'security', 'code', 'ux', 'ui'
$Building = 'build', 'review', 'deploy', 'approve', 'gate'
$MaxDeployAttempts = 5
$MaxDeployFixes = 2
$EventKinds = 'idea', 'stage', 'agent-start', 'agent-stop', 'gate', 'merge', 'cost', 'note', 'eval', 'feedback', 'deploy', 'destroy', 'lesson', 'canary'

$BoardLease = $null

function Fail([string] $msg, [int] $code = 1) {
    [Console]::Error.WriteLine("factory: $msg")
    # A refused command must not leave the board's lease held for its 60 s (see "State backends").
    if ($script:BoardLease) { Exit-BoardLease }
    exit $code
}

function Now { (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }

# --- locations -------------------------------------------------------------------------------

function Get-MainRoot {
    if ($env:FACTORY_REPO) { return $env:FACTORY_REPO }
    $common = git rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $common) { Fail 'not inside a git repository' }
    Split-Path ($common -replace '/', '\') -Parent
}

$MainRoot = $null
function MainRoot { if (-not $script:MainRoot) { $script:MainRoot = Get-MainRoot }; $script:MainRoot }

$Home_ = if ($env:FACTORY_HOME) { $env:FACTORY_HOME } else { Join-Path (MainRoot) '.factory-runs' }
New-Item -ItemType Directory -Force $Home_ | Out-Null
$BoardPath = Join-Path $Home_ 'board.json'
$EventsPath = Join-Path $Home_ 'events.jsonl'

. (Join-Path $PSScriptRoot 'github.ps1')
. (Join-Path $PSScriptRoot 'llm.ps1')
. (Join-Path $PSScriptRoot 'hcp.ps1')
. (Join-Path $PSScriptRoot 'deploy.ps1')
. (Join-Path $PSScriptRoot 'canary.ps1')
. (Join-Path $PSScriptRoot 'state.ps1')
try { Initialize-State -StateHome $Home_ -EnvFile (EnvFilePath) } catch { Fail "$($_.Exception.Message)" }

# Strangler routing (contract, "Implementations"): with FACTORY_IMPL=dotnet a ported command runs in
# the .NET CLI, with the same arguments and environment, on either state backend. It is forwarded before
# the board lock is taken: the CLI takes the same mutex (and in azure the same lease) itself.
$Ported = @('status', 'approve', 'edit-spec', 'rework', 'purge', 'set-stage', 'set-review', 'read-reviews', 'read-decision', 'event',
    'new-idea', 'add-spec', 'idea-done', 'idea-abandon', 'check-target', 'clone-target', 'list-repos',
    'checkout', 'publish', 'gate', 'gate-check', 'provenance', 'merge', 'sync-prs', 'set-cost', 'next')
if ($env:FACTORY_IMPL -and $env:FACTORY_IMPL -notin 'ps', 'dotnet') { Fail "FACTORY_IMPL '$env:FACTORY_IMPL' must be ps or dotnet" }
if ($env:FACTORY_IMPL -eq 'dotnet' -and $Command -in $Ported) {
    $cli = if ($env:FACTORY_CLI) { $env:FACTORY_CLI } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/bin/Factory.Cli/debug/Factory.Cli.dll')) }
    if (-not (Test-Path $cli)) { Fail "FACTORY_IMPL=dotnet, but the .NET CLI is not built at $cli (dotnet build src/factory)" }
    $fwd = @($Command)
    foreach ($a in $Arg1, $Arg2, $Arg3) { if (-not $a) { break }; $fwd += $a }
    foreach ($k in $PSBoundParameters.Keys | Where-Object { $_ -notin 'Command', 'Arg1', 'Arg2', 'Arg3' }) {
        $v = $PSBoundParameters[$k]
        if ($v -is [switch]) { if ($v) { $fwd += "-$k" } } else { $fwd += "-$k", "$v" }
    }
    # The canary fingerprint hashes the deploy scripts beside this one, and the templates beside this repo.
    $env:FACTORY_SCRIPTS = $PSScriptRoot
    # The CLI writes UTF-8 whatever the console's code page; read it back as UTF-8.
    $enc = [Console]::OutputEncoding
    try { [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); & dotnet $cli @fwd } finally { [Console]::OutputEncoding = $enc }
    exit $LASTEXITCODE
}

# A job's local clone of its target, where it builds and is gated; deleted at done or discarded.
function WorkRoot {
    if ($env:FACTORY_WORK) { return $env:FACTORY_WORK }
    Join-Path ([IO.Path]::GetTempPath()) 'ai-factory'
}

function WorkDir($j) { Join-Path (WorkRoot) "jobs/$($j.id)" }

# Returns $null for a target written owner/name, or why it is refused.
function Test-Target([string] $t) {
    if (-not $t) { return 'a target repo (owner/name) is required' }
    if ($t -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') { return "target '$t' is not a GitHub repo written owner/name" }
    $null
}

# A new target gets scaffolded by a bootstrap job: the repo is missing, or has no main. Fails on a GitHub error.
function Test-NewTarget([string] $t, [string] $what) {
    $state = Get-TargetState $t
    if ($state -like 'error: *') { Fail "$what refused: $($state.Substring(7))" }
    $state -ne 'existing'
}

# Returns $null for valid scaffold options (see "Targets" in the contract), or why they are refused.
function Test-Scaffold($sc) {
    if ($sc -isnot [Collections.IDictionary]) { return 'the scaffold options must be a JSON object' }
    if ("$($sc.name)" -cnotmatch '^[a-z][a-z0-9]*$') { return "scaffold name '$($sc.name)' must be lower-case letters and digits, starting with a letter" }
    if ("$($sc.resource)" -cnotmatch '^[A-Z][A-Za-z0-9]*$') { return "scaffold resource '$($sc.resource)' must be PascalCase" }
    if ($sc.db -cnotin 'sqlite', 'sqlserver', 'postgres') { return "scaffold db '$($sc.db)' must be sqlite, sqlserver or postgres" }
    if ($sc.ui -cnotin 'react', 'blazor', 'blazor-maui', 'none') { return "scaffold ui '$($sc.ui)' must be react, blazor, blazor-maui or none" }
    if ($sc.Contains('events') -and $sc.events -isnot [bool]) { return 'scaffold events must be true or false' }
    if ($sc.Contains('auth') -and $sc.auth -isnot [bool]) { return 'scaffold auth must be true or false' }
    if ($sc.Contains('auth') -and $sc.auth -and $sc.ui -eq 'none') { return 'scaffold auth needs a web front end (react, blazor or blazor-maui) for sign-in to redirect back to' }
    $null
}

function TemplatesRoot {
    if ($env:FACTORY_TEMPLATES) { return $env:FACTORY_TEMPLATES }
    Join-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) 'template-projects'
}

# The commands that scaffold a project from the templates, in order; a bootstrap job's builder and
# the canary run the same ones.
function Scaffold-Commands($sc) {
    $cmds = @("dotnet new aspire-base -n $($sc.name) -o . --dbProvider $($sc.db) --resource $($sc.resource)")
    # aspire-addon reads name, resource and data store (so the event store) from .aspirestarter.json.
    switch ($sc.ui) {
        'react' { $cmds += 'aspire-addon add ui-react' }
        'blazor' { $cmds += 'aspire-addon add ui-blazor --web true --maui false' }
        'blazor-maui' { $cmds += 'aspire-addon add ui-blazor --web true --maui true' }
    }
    if ($sc.events) { $cmds += 'aspire-addon add events' }
    # After the front end: auth reads which one from the manifest.
    if ($sc.Contains('auth') -and $sc.auth) { $cmds += 'aspire-addon add auth' }
    $cmds
}

# The bootstrap job's spec: factory/templates/bootstrap.md with the human's scaffold options filled in.
function Bootstrap-Spec($sc, [string] $target) {
    $n = $sc.name; $r = $sc.resource
    $cmds = Scaffold-Commands $sc
    $gate = @("- build: ``dotnet build $n.slnx``", "- test: ``dotnet test $n.slnx``", '- apphost: `dotnet build .aspire/apphost.cs`',
              '- infra: `aspire-addon infra check`')
    if ($sc.ui -eq 'react') { $gate += '- ui: `Set-Location src/ui/react; if (-not (Test-Path node_modules/.package-lock.json) -or (Get-Item package-lock.json).LastWriteTime -gt (Get-Item -Force node_modules/.package-lock.json).LastWriteTime) { npm ci }; npm run build`' }
    $text = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'templates/bootstrap.md') -Raw
    $values = @{
        name = $n; resource = $r; db = $sc.db; ui = $sc.ui; events = "$([bool]$sc.events)".ToLowerInvariant()
        auth = "$([bool]($sc.Contains('auth') -and $sc.auth))".ToLowerInvariant()
        target = $target; templates = (TemplatesRoot); gate = ($gate -join "`n")
        commands = (($cmds | ForEach-Object { "  - ``$_``" }) -join "`n")
    }
    foreach ($k in $values.Keys) { $text = $text.Replace("{{$k}}", $values[$k]) }
    $text -split "`r?`n"
}

# Reads `## Target` (- Repo: <path>) and `## Gate` (- <name>: `<cmd>`) from a spec file.
function Read-SpecMeta([string] $path) {
    $meta = @{ repo = $null; gate = @() }
    if (-not (Test-Path $path)) { return $meta }
    $section = ''
    foreach ($line in Get-Content $path) {
        if ($line -match '^##\s+(.+?)\s*$') { $section = $Matches[1].ToLowerInvariant(); continue }
        if ($section -eq 'target' -and $line -match '^\s*-\s*Repo:\s*(.+?)\s*$') { $meta.repo = $Matches[1] }
        if ($section -eq 'gate' -and $line -match '^\s*-\s*([a-z0-9-]+):\s*`(.+)`\s*$') {
            $meta.gate += [pscustomobject]@{ name = $Matches[1]; cmd = $Matches[2] }
        }
    }
    $meta
}

# The spec's Scope: In globs: the first backticked span of each sub-bullet under `## Scope` `- In:`
# (see "Targets" in the contract). Prose, and inline backticks after the glob, are not globs.
# Returns the globs and, as .At, the index of the line after which a widening glob goes (-1: no In line).
function Read-ScopeIn([string[]] $lines) {
    $globs = [Collections.Generic.List[string]]::new()
    $at = -1; $sec = $false; $in = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^#{1,6}\s+(.*?)\s*$') { $sec = $Matches[1] -eq 'Scope'; $in = $false; continue }
        if (-not $sec) { continue }
        if ($line -match '^[-*]\s*In:') { $in = $true; $at = $i; continue }
        if ($line -match '^\S') { $in = $false; continue }
        if ($in -and $line -match '^\s+[-*]\s+`([^`]+)`') { $globs.Add($Matches[1].Trim()); $at = $i }
        elseif ($in -and $line -match '^\s+\S') { $at = $i }
    }
    [pscustomobject]@{ Globs = @($globs); At = $at }
}

# A Scope glob as a regex: `*` matches within one path segment, `**` across segments.
function Glob-Regex([string] $g) {
    $r = [Text.StringBuilder]::new('^')
    for ($i = 0; $i -lt $g.Length; $i++) {
        $c = $g[$i]
        if ($c -eq '*' -and $i + 1 -lt $g.Length -and $g[$i + 1] -eq '*') {
            $i++
            if ($i + 1 -lt $g.Length -and $g[$i + 1] -eq '/') { $i++; [void]$r.Append('(?:.*/)?') } else { [void]$r.Append('.*') }
        }
        elseif ($c -eq '*') { [void]$r.Append('[^/]*') }
        elseif ($c -eq '?') { [void]$r.Append('[^/]') }
        else { [void]$r.Append([regex]::Escape([string]$c)) }
    }
    $r.Append('$').ToString()
}

function JobDir([string] $id) { Join-Path $Home_ "jobs/$id" }

# --- board I/O (serialized across processes, atomic write) ------------------------------------

$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Home_.ToLowerInvariant()))).Substring(0, 16)
$Mutex = [Threading.Mutex]::new($false, "factory-board-$hash")

# The board lock: the mutex serializes this machine, and in azure the lease on locks/board serializes
# machines. -Seconds -1 waits as long as it takes (re-taking it after a gate, deploy or canary).
function Enter-BoardLock([int] $Seconds = 30) {
    if ($Seconds -ge 0 -and $env:FACTORY_BOARD_LOCK_SECONDS) { $Seconds = [int]$env:FACTORY_BOARD_LOCK_SECONDS }
    # A holder that died leaves the mutex abandoned, and ours. Timing out must refuse: running on
    # unlocked, this command's save could overwrite another's.
    $got = try { $Mutex.WaitOne($(if ($Seconds -lt 0) { -1 } else { $Seconds * 1000 })) } catch [Threading.AbandonedMutexException] { $true }
    if (-not $got) { Fail "board lock refused: another factory.ps1 has held the board for over $Seconds s" }
    if (-not (State-IsAzure)) { return }
    $wait = if ($env:FACTORY_STATE_LOCK_SECONDS) { [int]$env:FACTORY_STATE_LOCK_SECONDS } elseif ($Seconds -lt 0) { 600 } else { $Seconds }
    try { $script:BoardLease = Enter-StateLease 'board' $wait } catch { $Mutex.ReleaseMutex(); Fail "$($_.Exception.Message)" }
    if (-not $script:BoardLease) { $Mutex.ReleaseMutex(); Fail "state lock refused: another machine has held the board (locks/board) for over $wait s" }
}

function Exit-BoardLease {
    $l = $script:BoardLease
    $script:BoardLease = $null
    if ($l) { try { Exit-StateLease 'board' $l } catch { } }
}

function Exit-BoardLock { Exit-BoardLease; $Mutex.ReleaseMutex() }

Enter-BoardLock

function Load-Board {
    # In azure, the container's board wins; when it changed, so may everything else.
    if ((State-IsAzure) -and $Command -ne 'state-migrate') {
        try { if ((Sync-Board) -eq 'changed') { $null = Pull-State } } catch { Fail "$($_.Exception.Message)" }
    }
    # Ordered: a plain hashtable's key order changes from one process to the next (contract, "board.json").
    if (-not (Test-Path $BoardPath)) { return [ordered]@{ version = 2; ideas = @(); jobs = @() } }
    $b = Get-Content $BoardPath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    if (-not $b.ContainsKey('ideas')) { $b.ideas = @() }
    $b
}

# Replaces $path with $tmp. A reader that opened $path without delete sharing (serve.ps1 serving
# /state/board.json to the dashboard poll) makes the replace fail for a moment, so it is retried.
function Replace-File([string] $tmp, [string] $path) {
    for ($i = 0; ; $i++) {
        try { [IO.File]::Move($tmp, $path, $true); return }
        catch [UnauthorizedAccessException], [IO.IOException] { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 50 }
    }
}

function Save-Board($b) {
    if (State-IsAzure) {
        if (-not $script:BoardLease -or -not (Update-StateLease 'board' $script:BoardLease)) { Fail 'state lock refused: the board lease lapsed and another machine took it' }
        try { Save-StateBoard ($b | ConvertTo-Json -Depth 20) } catch { Fail "$($_.Exception.Message)" }
        return
    }
    $tmp = "$BoardPath.$PID.tmp"
    [IO.File]::WriteAllText($tmp, ($b | ConvertTo-Json -Depth 20))
    Replace-File $tmp $BoardPath
}

function Add-Event([hashtable] $e) {
    $o = [ordered]@{ ts = Now }
    foreach ($k in $e.Keys) { $o[$k] = $e[$k] }
    try { Add-StateEvent ($o | ConvertTo-Json -Compress -Depth 10) } catch { Fail "$($_.Exception.Message)" }
}

# A subagent's transcript lives under ~/.claude on the machine that ran it; in azure, its agent-stop
# copies it into the job's folder so the push takes it to the container.
function Copy-Transcript($e) {
    if (-not (State-IsAzure) -or $e.kind -ne 'agent-stop' -or -not $e['job']) { return }
    $start = Get-Content $EventsPath -ErrorAction SilentlyContinue | Where-Object { $_ -match '"kind":"agent-start"' } |
        ForEach-Object { try { $_ | ConvertFrom-Json -AsHashtable } catch { } } |
        Where-Object { $_['job'] -eq $e.job -and $_['agent'] -eq $e['agent'] -and $_['transcript'] } | Select-Object -Last 1
    if (-not $start -or -not (Test-Path -LiteralPath $start.transcript)) { return }
    $dir = Join-Path (JobDir $e.job) 'transcripts'
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item -LiteralPath $start.transcript (Join-Path $dir "agent-$($start['agentId']).jsonl") -Force
}

# Every command ends here in azure, still under the board lock: push what changed, then hold the
# container to its cap. A live job's run logs wait until it leaves the Building stages.
function Sync-Up {
    $live = @($Board.jobs | Where-Object { $_.stage -in $Building } | ForEach-Object { $_.id })
    $n = Push-State -Skip { param($rel) $rel -match '^jobs/([^/]+)/run-[^/]*\.jsonl$' -and $Matches[1] -in $live }.GetNewClosure()
    if ($n -eq 0) { return }
    $r = Invoke-StatePrune -Jobs $Board.jobs
    if ($r.Deleted) { Add-Event ([ordered]@{ kind = 'note'; detail = "pruned $($r.Deleted) blobs, $($r.Freed) bytes freed" }) }
}

$Board = Load-Board

function Get-Job([string] $id) {
    $j = $Board.jobs | Where-Object { $_.id -eq $id } | Select-Object -First 1
    if (-not $j) { Fail "no job $id" }
    $j
}

# The job $j is stacked on (see "Stacked jobs" in the contract), or $null. A base purged from the
# board was finished, so it counts as merged.
function Base-Job($j) {
    if (-not ($j.Contains('base') -and $j.base)) { return $null }
    $Board.jobs | Where-Object { $_.id -eq $j.base } | Select-Object -First 1
}

# Where $j's branch starts and its PR points: the base's branch while the base is unmerged, else main.
function Base-Branch($j) {
    $b = Base-Job $j
    if ($b -and $b.stage -ne 'done') { "factory/$($b.id)" } else { 'main' }
}

# Why job $id cannot be a base for work on target $t, or $null.
function Test-Base([string] $t, [string] $id) {
    $b = $Board.jobs | Where-Object { $_.id -eq $id } | Select-Object -First 1
    if (-not $b) { return "no job $id" }
    if ($b.target -ne $t) { return "$id is on $($b.target), not $t" }
    if ($b.stage -in 'done', 'discarded') { return "$id is $($b.stage); start from main" }
    if (-not ($b.Contains('pr') -and $b.pr)) { return "$id has no PR yet, so there is no branch to build on" }
    $null
}

function Get-Idea([string] $id) {
    $i = $Board.ideas | Where-Object { $_.id -eq $id } | Select-Object -First 1
    if (-not $i) { Fail "no idea $id" }
    $i
}

# board.seq[prefix] is the highest id ever allocated, so ids that purge removed are never reused.
function Update-Seq([string] $prefix, $items) {
    if (-not $Board.Contains('seq') -or $Board.seq -isnot [Collections.IDictionary]) { $Board.seq = [ordered]@{} }
    $max = [int]($Board.seq[$prefix] ?? 0)
    foreach ($x in $items) { if ($x.id -match "^$prefix-(\d+)$") { $max = [Math]::Max($max, [int]$Matches[1]) } }
    $Board.seq[$prefix] = $max
    $max
}

function Next-Id([string] $prefix, $items) {
    $n = (Update-Seq $prefix $items) + 1
    $Board.seq[$prefix] = $n
    '{0}-{1:D3}' -f $prefix, $n
}

# Creates a backlog job from spec lines (heading '# Spec: <title>'); returns its id.
function Add-Job($i, [string[]] $lines, [string] $source, [string] $kind) {
    $h = [Array]::FindIndex([string[]]$lines, [Predicate[string]] { param($l) $l -match '^#\s*Spec:' })
    if ($h -lt 0) { Fail "no '# Spec:' heading in $source" }
    $title = ($lines[$h] -replace '^#\s*Spec:\s*', '').Trim()
    if ($title -match '\s[—-]\s(.+)$') { $title = $Matches[1].Trim() }
    if (-not $title) { Fail "empty title in $source" }
    $id = Next-Id 'J' $Board.jobs
    $lines[$h] = "# Spec: $id — $title"
    New-Item -ItemType Directory -Force (JobDir $id) | Out-Null
    $spec = Join-Path (JobDir $id) 'spec.md'
    Set-Content $spec $lines
    $ts = Now
    $j = [ordered]@{
        id = $id; idea = $i.id; kind = $kind; title = $title; stage = 'backlog'; round = 1; branch = "factory/$id"
        base = $(if ($i.Contains('base')) { $i.base } else { $null })
        target = (Read-SpecMeta $spec).repo
        pr = $null; reviews = $null; decision = $null; gate = $null; flag = $null; merged = $null; approvedAt = $null
        costUsd = $null; durationMs = $null; createdAt = $ts; updatedAt = $ts; history = @()
    }
    Reset-Reviews $j
    $Board.jobs = @($Board.jobs) + $j
    $i.jobs = @($i.jobs) + $id
    Save-Board $Board
    Add-Event ([ordered]@{ job = $id; kind = 'stage'; from = $null; to = 'backlog'; by = 'orchestrator'; idea = $i.id })
    $id
}

function Reset-Reviews($j) { $j.reviews = [ordered]@{ security = 'pending'; code = 'pending'; ux = 'pending'; ui = 'pending' } }

function Move-Job($j, [string] $to, [string] $by, [string] $reason = $null, [string] $detail = $null, [string] $why = $null) {
    $from = $j.stage
    $ts = Now
    $j.stage = $to
    $j.updatedAt = $ts
    $j.flag = if ($reason) { [ordered]@{ reason = $reason; detail = $detail } } else { $null }
    if ($reason -and $why) { $j.flag.why = $why }
    # Every move into build starts a round; checkout records the new round's start.
    if ($to -eq 'build' -and $j['roundStart']) { $j.roundStart = $null }
    $j.history = @($j.history) + [ordered]@{ ts = $ts; from = $from; to = $to; by = $by }
    Save-Board $Board
    $e = [ordered]@{ job = $j.id; kind = 'stage'; from = $from; to = $to; by = $by }
    if ($reason) { $e.reason = $reason }
    Add-Event $e
}

# GitHub dispatches only workflows on the default branch, and a bootstrap is what puts deploy.yml there.
function Is-Bootstrap($j) { $j.Contains('kind') -and $j.kind -eq 'bootstrap' }
$BootstrapNoDeploy = "deploy skipped: bootstrap (deploy.yml reaches main only when this merges; the first feature job gets the first preview)"

function Require-Stage($j, [string[]] $stages, [string] $what) {
    if ($j.stage -notin $stages) { Fail "$what refused: $($j.id) is in '$($j.stage)', needs $($stages -join '|')" }
}

# The body of a markdown file's '## <heading>' section, trimmed; $null when absent, empty or N/A.
function Read-Section([string] $path, [string] $heading) {
    if (-not (Test-Path $path)) { return $null }
    $text = (Get-Content $path -Raw) -replace "`r", ''
    if ($text -notmatch "(?ms)^## $([regex]::Escape($heading))[ \t]*\n(.*?)(?=^## |\z)") { return $null }
    $body = $Matches[1].Trim()
    if (-not $body -or $body -eq 'N/A') { return $null }
    $body
}

function Read-FirstLine([string] $path) {
    if (-not (Test-Path $path)) { return $null }
    $line = Get-Content $path -TotalCount 1
    if ($null -eq $line) { return $null }
    $line.TrimEnd()
}

# A usage limit holds for every job, so the queue waits for the human's resume (see "Runner").
function Usage-Limited { [bool]($Board.jobs | Where-Object { $_['flag'] -and $_.flag.reason -eq 'usage-limit' }) }

function Next-Runnable {
    if ($Board.jobs | Where-Object { $_.stage -in $Building }) { return $null }
    if (Usage-Limited) { return $null }
    $Board.jobs | Where-Object { $_.stage -eq 'approved' } |
        Sort-Object { $_.approvedAt }, { $_.id } | Select-Object -First 1
}

function Is-Deployable($j) { (Deploys-On) -and -not (Is-Bootstrap $j) }

# Where a job the factory gives up on waits for a human (see "Blocked" in the contract): a deployable
# job that never reached a passing preview parks in blocked; one in approve or gate has its preview up.
# deploy-fix and block need a failed attempt, so an agent cannot skip the deploy.
function Require-FailedDeploy($j, [string] $command) {
    Require-Stage $j 'deploy' $command
    if (-not $j['deploy'] -or $j.deploy.status -ne 'failed') { Fail "$command refused: $($j.id) has no failed deploy attempt" }
}

function Escalation-Stage($j) { if ((Is-Deployable $j) -and $j.stage -in 'build', 'review', 'deploy') { 'blocked' } else { 'ready' } }

# Whether the runner may take $j: it heads the queue, or `next` chose it past jobs held for a canary.
function Is-Next($j) {
    $n = Next-Runnable
    ($n -and $n.id -eq $j.id) -or (Next-Gated $false) -eq $j.id
}

# What `next` prints: the oldest approved job the runner may take, skipping deployable jobs until a
# canary has passed for the current fingerprint (see "Canary" in the contract). With -NeedsCanary,
# 'canary' when a deployable job is held back only because no canary has run for these inputs.
function Next-Gated([bool] $needsCanary) {
    if ($Board.jobs | Where-Object { $_.stage -in $Building }) { return $null }
    if (Usage-Limited) { return $null }
    $queue = @($Board.jobs | Where-Object { $_.stage -eq 'approved' } | Sort-Object { $_.approvedAt }, { $_.id })
    $state = $null
    foreach ($j in $queue) {
        if (-not (Is-Deployable $j)) { return $j.id }
        if (-not $state) { $state = Canary-State }
        if ($state -eq 'pass') { return $j.id }
        if ($needsCanary -and $state -eq 'none') { return 'canary' }
    }
    $null
}

# --- git helpers ----------------------------------------------------------------------------

function Invoke-Git([string] $Where) {
    $out = & git -C $Where @args 2>&1
    [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($out | Out-String).Trim() }
}

# Deletes the job's work dir. A locked file (a build server, an editor) is a note event, never a
# failure; purge tries again.
function Remove-WorkDir($j) {
    $wt = WorkDir $j
    if (-not (Test-Path $wt)) { return }
    try { Remove-Item -LiteralPath $wt -Recurse -Force -ErrorAction Stop }
    catch { Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = "work dir not removed: $($_.Exception.Message)" }) }
}

# Closes the job's open PR, deletes factory/<id> on GitHub if it was pushed, and deletes the work dir.
function Remove-JobWork($j) {
    if ($j.Contains('target') -and -not (Test-Target $j.target)) {
        $pr = Get-OpenPr $j.target "factory/$($j.id)"
        if ($pr) { Invoke-GitHub PATCH "/repos/$($j.target)/pulls/$($pr.number)" @{ state = 'closed' } | Out-Null }
        Invoke-GitHub DELETE "/repos/$($j.target)/git/refs/heads/factory/$($j.id)" | Out-Null
    }
    Remove-WorkDir $j
}

# Creates the target on GitHub if it is missing, and pushes main with an empty initial commit if it has none.
function Ensure-GitHubRepo([string] $t) {
    $err = Test-Target $t
    if ($err) { return $err }
    $state = Get-TargetState $t
    if ($state -like 'error: *') { return $state.Substring(7) }
    if ($state -eq 'existing') { return $null }
    if ($state -eq 'missing') { $err = New-GitHubRepo $t; if ($err) { return $err } }
    $seed = Join-Path (WorkRoot) "seed-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    try {
        New-Item -ItemType Directory -Force $seed | Out-Null
        $r = Invoke-Git $seed init -q -b main
        if ($r.Code -eq 0) { $r = Invoke-Git $seed -c user.name=factory -c user.email=factory@localhost commit -q --allow-empty -m 'Initial commit (factory)' }
        if ($r.Code -eq 0) { $r = Invoke-GitAuth $seed push -q (CloneUrl $t) main }
        if ($r.Code -ne 0) { return "initial commit for $t failed: $($r.Out)" }
    } finally { Remove-Item -LiteralPath $seed -Recurse -Force -ErrorAction SilentlyContinue }
    $null
}

# Clones the job's target into its work dir (once) and checks out factory/<id>: the pushed branch
# after a rework, otherwise a new one from origin/main, or from its unmerged base's branch.
function Ensure-Clone([string] $id) {
    $j = Get-Job $id
    $err = Test-Target $j.target
    if ($err) { return $err }
    $wt = WorkDir $j
    $branch = "factory/$id"
    if (-not (Test-Path (Join-Path $wt '.git'))) {
        $err = Ensure-GitHubRepo $j.target
        if ($err) { return $err }
        Remove-Item -LiteralPath $wt -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force (Split-Path $wt -Parent) | Out-Null
        $r = Invoke-GitAuth (Split-Path $wt -Parent) clone -q (CloneUrl $j.target) $wt
        if ($r.Code -ne 0) { return "clone of $($j.target) failed: $($r.Out)" }
    }
    $current = (Invoke-Git $wt branch --show-current).Out
    if ($current -eq $branch) { return $null }
    if ((Invoke-Git $wt status --porcelain).Out) { return "work dir $wt has uncommitted changes on '$current'" }
    $b = Base-Job $j
    if ($b -and $b.stage -eq 'discarded') { return "$id is stacked on $($b.id), which was discarded" }
    $start = Base-Branch $j
    $r = if ((Invoke-Git $wt rev-parse --verify --quiet "refs/remotes/origin/$branch").Code -eq 0) { Invoke-Git $wt checkout -q -B $branch "origin/$branch" }
         elseif ((Invoke-Git $wt rev-parse --verify --quiet "refs/remotes/origin/$start").Code -eq 0) { Invoke-Git $wt checkout -q -b $branch "origin/$start" }
         else { return "$($j.target) has no $start branch" }
    if ($r.Code -ne 0) { return "checkout $branch failed: $($r.Out)" }
    $null
}

# Pushes factory/<id> and opens its PR into main if it has none; returns the PR url, or an error string
# prefixed 'error: '.
function Publish-Job($j) {
    $wt = WorkDir $j
    $branch = "factory/$($j.id)"
    if (-not (Test-Path (Join-Path $wt '.git'))) { return "error: $($j.id) has no work dir to publish" }
    $r = Invoke-GitAuth $wt push -q origin "$($branch):$branch"
    if ($r.Code -ne 0) { return "error: push failed: $($r.Out)" }
    $pr = Get-OpenPr $j.target $branch
    if (-not $pr) {
        $res = Invoke-GitHub POST "/repos/$($j.target)/pulls" @{ title = "$($j.id): $($j.title)"; head = $branch; base = (Base-Branch $j)
            body = "Built by the factory from spec $($j.id). Merged when a human moves it to Done." }
        if ($res.Status -ne 201) { return "error: cannot open the PR: $(GhError $res)" }
        $pr = [ordered]@{ number = [int]$res.Body.number; url = $res.Body.html_url }
        Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = "PR opened: $($pr.url)" })
    }
    $j.pr = $pr
    Save-Board $Board
    $pr.url
}

function Gate-Commands($j) {
    if ($env:FACTORY_GATE_CMDS) { return @($env:FACTORY_GATE_CMDS | ConvertFrom-Json) }
    @((Read-SpecMeta (Join-Path (JobDir $j.id) 'spec.md')).gate)
}

# pwsh splits an unquoted native argument like -var-file=envs/ephemeral.tfvars at the dot (J-022), so
# such a token is quoted. No cmdlet takes -name=value, so this only ever touches native arguments.
function Quote-NativeArgs([string] $cmd) {
    $cmd -replace '(?<=^|[\s;|(])(-[A-Za-z][\w-]*=[^\s"''`;|()]*\.[^\s"''`;|()]*)(?=$|[\s;|)])', '"$1"'
}

# Runs one gate command in $wt, as both gate and gate-check do; returns { Code, Out }.
function Invoke-GateCommand($c, [string] $wt) {
    # npm and vite write UTF-8; a runner started by serve.ps1 has a fresh console on the OEM codepage.
    $enc = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    Push-Location $wt
    try {
        $out = & pwsh -NoProfile -Command (Quote-NativeArgs $c.cmd) 2>&1
        [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($out | Out-String) }
    } finally { Pop-Location; [Console]::OutputEncoding = $enc }
}

# Takes jobs/<id>/gate-check.lock, waiting up to FACTORY_GATE_CHECK_WAIT_MINUTES (default 20).
# Returns the open stream (dispose it to release), or $null on timeout.
function Enter-GateCheckLock([string] $path) {
    $minutes = if ($env:FACTORY_GATE_CHECK_WAIT_MINUTES) { [double]$env:FACTORY_GATE_CHECK_WAIT_MINUTES } else { 20 }
    $until = (Get-Date).AddMinutes($minutes)
    while ($true) {
        try { return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException], [UnauthorizedAccessException] { if ((Get-Date) -gt $until) { return $null }; Start-Sleep -Milliseconds 200 }
    }
}

# Runs the gate in the work dir on factory/<id> and records exit codes and sha on the job.
# Returns $true when it ran (green or red), or an error string. The board lock is released while
# the commands run, so callers must re-fetch the job afterwards.
function Invoke-Gate([string] $id) {
    $err = Ensure-Clone $id
    if ($err) { return $err }
    $j = Get-Job $id
    $wt = WorkDir $j
    $cmds = @(Gate-Commands $j)
    if (-not $cmds) { return 'no gate commands in the spec' }
    $log = Join-Path (JobDir $j.id) 'gate.log'
    New-Item -ItemType Directory -Force (JobDir $j.id) | Out-Null
    Set-Content $log "# gate $($j.id) at $(Now)"
    $result = [ordered]@{}
    Exit-BoardLock
    try {
    foreach ($c in $cmds) {
        Add-Content $log "`n## $($c.name): $($c.cmd)"
        $r = Invoke-GateCommand $c $wt
        Add-Content $log $r.Out
        Add-Content $log "exit $($r.Code)"
        $result[$c.name] = $r.Code
    }
    } finally {
        Enter-BoardLock -1
        $script:Board = Load-Board
    }
    $j = Get-Job $id
    $result.sha = (Invoke-Git $wt rev-parse HEAD).Out
    $result.at = Now
    $j.gate = $result
    $detail = ($result.Keys | Where-Object { $_ -notin 'sha', 'at' } | ForEach-Object { "$_=$($result[$_])" }) -join ' '
    Save-Board $Board
    Add-Event ([ordered]@{ job = $j.id; kind = 'gate'; detail = "$detail sha=$($result.sha.Substring(0, [Math]::Min(7, $result.sha.Length)))" })
    $true
}

function Gate-Green($j) {
    if (-not $j.gate) { return $false }
    foreach ($k in $j.gate.Keys) { if ($k -notin 'sha', 'at' -and $j.gate[$k] -ne 0) { return $false } }
    $head = (Invoke-Git (WorkDir $j) rev-parse --verify --quiet "factory/$($j.id)").Out
    $j.gate.sha -eq $head
}

# After the PR merged as $sha: deletes factory/<id> on GitHub, records the merge and its paths, moves
# the job to done, deletes the work dir, and releases a bootstrap's idea.
function Complete-Merge($j, [string] $sha) {
    # GitHub closes a PR whose base branch is deleted: point the stacked jobs' PRs at main first.
    foreach ($k in @($Board.jobs | Where-Object { $_.Contains('base') -and $_.base -eq $j.id -and $_.stage -notin 'done', 'discarded' -and $_.Contains('pr') -and $_.pr })) {
        $res = Invoke-GitHub PATCH "/repos/$($k.target)/pulls/$($k.pr.number)" @{ base = 'main' }
        $detail = if ($res.Status -eq 200) { "PR #$($k.pr.number) retargeted to main: $($j.id) merged" } else { "PR #$($k.pr.number) not retargeted to main: $(GhError $res)" }
        Add-Event ([ordered]@{ job = $k.id; kind = 'note'; detail = $detail })
    }
    Invoke-GitHub DELETE "/repos/$($j.target)/git/refs/heads/factory/$($j.id)" | Out-Null
    $wt = WorkDir $j
    if (-not (Test-Path (Join-Path $wt '.git'))) { Ensure-Clone $j.id | Out-Null }
    Invoke-GitAuth $wt fetch -q origin main | Out-Null
    $added = @(); $modified = @()
    foreach ($line in (Invoke-Git $wt -c core.quotepath=false diff --name-status --no-renames "$sha^1" $sha).Out -split '\r?\n') {
        if ($line -notmatch '^([A-Z])\t(.+)$') { continue }
        if ($Matches[1] -eq 'A') { $added += $Matches[2] } elseif ($Matches[1] -eq 'M') { $modified += $Matches[2] }
    }
    $j.merged = [ordered]@{ sha = $sha; repo = $j.target; pr = $j.pr.url; added = $added; modified = $modified }
    Move-Job $j 'done' 'human'
    Add-Event ([ordered]@{ job = $j.id; kind = 'merge'; detail = $sha })
    # Build check 2: the open siblings of the same idea were specced against the old main (J-022).
    $sibs = @(if ($j.idea) { $Board.jobs | Where-Object { $_.id -ne $j.id -and $_['idea'] -eq $j.idea -and $_.stage -notin 'done', 'discarded' } })
    foreach ($k in $sibs) { $k.baseMoved = [ordered]@{ by = $j.id; sha = $sha; at = Now } }
    if ($sibs) {
        Save-Board $Board
        foreach ($k in $sibs) { Add-Event ([ordered]@{ job = $k.id; kind = 'note'; detail = "base moved: $($j.id) merged" }) }
    }
    Remove-WorkDir $j
    Start-Destroy $j
    # A merged bootstrap releases its idea for a /factory:idea session.
    $i = if ($j.Contains('kind') -and $j.kind -eq 'bootstrap' -and $j.idea) { $Board.ideas | Where-Object { $_.id -eq $j.idea } | Select-Object -First 1 }
    if ($i -and $i.status -eq 'waiting') {
        $i.status = 'drafting'
        Save-Board $Board
        Add-Event ([ordered]@{ idea = $i.id; kind = 'idea'; detail = 'drafting' })
        "released $($i.id)"
    }
}

# Build check 3: the paths this round's own commits changed (base work merged in excluded) whose
# adding commit came from a template - reachable from the target's bootstrap merge, or a
# `scaffold:` / `addon:` commit. Returns { Paths, Note }; Paths is $null when the round has no start.
function Get-Provenance($j) {
    $wt = WorkDir $j
    if (-not $j['roundStart'] -or -not (Test-Path (Join-Path $wt '.git'))) { return @{ Paths = $null; Note = "no round start recorded for $($j.id)" } }
    $boot = @($Board.jobs | Where-Object { $_['kind'] -eq 'bootstrap' -and $_.target -eq $j.target -and $_['merged'] } | ForEach-Object { $_.merged.sha })
    if (-not $boot) {
        # A purged bootstrap job leaves its merge sha in its eval.
        $boot = @(Get-ChildItem (Join-Path $Home_ 'evals') -Filter 'J-*.json' -File -ErrorAction SilentlyContinue | ForEach-Object {
            try { Get-Content $_.FullName -Raw | ConvertFrom-Json -AsHashtable } catch { } } |
            Where-Object { $_ -and $_['kind'] -eq 'bootstrap' -and $_['target'] -eq $j.target -and $_['merged'] } | ForEach-Object { $_['merged'] })
    }
    $note = if (-not $boot) { "no bootstrap merge on record for $($j.target): only scaffold: and addon: commits count as template-sourced" }
    # `--not <base>`, not `^<base>`: the caret does not survive the trip through Invoke-Git intact.
    $excl = if ((Invoke-Git $wt rev-parse --verify --quiet refs/remotes/factory/base).Code -eq 0) { @('--not', 'refs/remotes/factory/base') } else { @() }
    $changed = (Invoke-Git $wt -c core.quotePath=false log --no-merges --format= --name-only "$($j.roundStart)..HEAD" @excl).Out -split '\r?\n' |
        Where-Object { $_ } | Sort-Object -Unique
    $paths = foreach ($p in $changed) {
        $add = ((Invoke-Git $wt log --diff-filter=A --format=%H '--' $p).Out -split '\r?\n' | Where-Object { $_ } | Select-Object -Last 1)
        if (-not $add) { continue }
        $subject = (Invoke-Git $wt log -1 --format=%s $add).Out
        $fromBoot = $boot | Where-Object { (Invoke-Git $wt merge-base --is-ancestor $add $_).Code -eq 0 }
        if ($subject -match '^(scaffold|addon):' -or $fromBoot) { $p }
    }
    @{ Paths = @($paths); Note = $note }
}

# The job's PR as GitHub returns it when the human has merged it there, else $null.
function Get-GitHubMerge($j) {
    if (-not $j.pr) { return $null }
    $pr = Get-Pr $j.target $j.pr.number
    if ($pr -and $pr.merged) { $pr }
}

# A PR merged on GitHub is the human's acceptance, gated or not; an ungated head is noted, not refused.
function Complete-GitHubMerge($j, $pr) {
    $gated = if ($j.gate) { $j.gate.sha }
    $detail = "PR #$($j.pr.number) merged on GitHub"
    if ($pr.head.sha -ne $gated) { $detail += " at $("$($pr.head.sha)".Substring(0, [Math]::Min(7, "$($pr.head.sha)".Length))), not the gated $(if ($gated) { $gated.Substring(0, 7) } else { 'head (no gate)' })" }
    Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = $detail })
    Complete-Merge $j $pr.merge_commit_sha
}

# --- commands -------------------------------------------------------------------------------

try {
    switch ($Command) {

        'new-idea' {
            if (-not $Arg1) { Fail 'usage: new-idea <text> -Target <owner/name> [-Scaffold <json>] [-Base <J-NNN>]' }
            $err = Test-Target $Target
            if ($err) { Fail "new-idea refused: $err" }
            $new = Test-NewTarget $Target 'new-idea'
            if ($Base) {
                if ($new) { Fail "base refused: '$Target' is a new repo" }
                $err = Test-Base $Target $Base
                if ($err) { Fail "base refused: $err" }
            }
            $sc = $null
            if ($new) {
                if (-not $Scaffold) { Fail "new-idea refused: '$Target' is a new repo, so it needs scaffold options" }
                try { $sc = $Scaffold | ConvertFrom-Json -AsHashtable } catch { Fail 'new-idea refused: the scaffold options are not valid JSON' }
                $err = Test-Scaffold $sc
                if ($err) { Fail "new-idea refused: $err" }
            } elseif ($Scaffold) { Fail "new-idea refused: '$Target' is an existing repo; scaffold options are only for a new one" }
            $status = if ($new) { 'waiting' } else { 'drafting' }
            $id = Next-Id 'I' $Board.ideas
            $dir = Join-Path $Home_ "ideas/$id"
            New-Item -ItemType Directory -Force $dir | Out-Null
            Set-Content (Join-Path $dir 'idea.md') (@($Arg1, '', "Target: $Target") + @(if ($Base) { "Base: factory/$Base (unmerged; this idea builds on it)" }))
            $i = [ordered]@{ id = $id; text = $Arg1; target = $Target; scaffold = $sc; base = $(if ($Base) { $Base } else { $null }); status = $status; jobs = @(); createdAt = Now }
            $Board.ideas = @($Board.ideas) + $i
            Save-Board $Board
            Add-Event ([ordered]@{ idea = $id; kind = 'idea'; detail = $status })
            if ($new) { "$id $(Add-Job $i (Bootstrap-Spec $sc $Target) 'the bootstrap template' 'bootstrap')" } else { $id }
        }

        'check-target' {
            $err = Test-Target $Arg1
            if ($err) { Fail "check-target refused: $err" }
            if (Test-NewTarget $Arg1 'check-target') { 'new' } else { 'existing' }
        }

        'list-repos' {
            $repos = Get-OwnedRepos
            if ($repos -is [string]) { Fail "list-repos refused: $($repos.Substring(7))" }
            ConvertTo-Json -Compress -InputObject @($repos)
        }

        'add-spec' {
            $i = Get-Idea $Arg1
            if (-not $Arg2 -or -not (Test-Path $Arg2)) { Fail "spec file not found: $Arg2" }
            Add-Job $i @(Get-Content $Arg2) $Arg2 'feature'
        }

        'idea-done' {
            $i = Get-Idea $Arg1
            $i.status = if ($Failed) { 'failed' } else { 'done' }
            if ($Failed) { $i.reason = $Reason }
            Save-Board $Board
            Add-Event ([ordered]@{ idea = $i.id; kind = 'idea'; detail = $i.status })
        }

        'idea-abandon' {
            $i = Get-Idea $Arg1
            if ($i.status -notin 'waiting', 'drafting', 'failed') { Fail "idea-abandon refused: $($i.id) is $($i.status)" }
            $i.status = 'abandoned'
            Save-Board $Board
            Add-Event ([ordered]@{ idea = $i.id; kind = 'idea'; detail = 'abandoned' })
        }

        'edit-spec' {
            $j = Get-Job $Arg1
            Require-Stage $j 'backlog' 'edit-spec'
            if (-not $File -or -not (Test-Path $File)) { Fail "spec file not found: $File" }
            Copy-Item $File (Join-Path (JobDir $j.id) 'spec.md') -Force
            $j.target = (Read-SpecMeta $File).repo
            $j.updatedAt = Now
            Save-Board $Board
            Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = 'spec edited' })
        }


        'approve' {
            $j = Get-Job $Arg1
            Require-Stage $j 'backlog' 'approve'
            $spec = Join-Path (JobDir $j.id) 'spec.md'
            if (-not (Test-Path $spec)) { Fail "$($j.id) has no spec.md" }
            $meta = Read-SpecMeta $spec
            if (-not $meta.repo) { Fail "approve refused: the spec has no '## Target' with '- Repo: <owner/name>'" }
            $err = Test-Target $meta.repo
            if ($err) { Fail "approve refused: $err" }
            if (-not $meta.gate) { Fail "approve refused: the spec's '## Gate' has no '- <name>: ``<command>``' lines" }
            $j.target = $meta.repo
            $j.approvedAt = Now
            Move-Job $j 'approved' 'human'
        }

        'rework' {
            $j = Get-Job $Arg1
            Require-Stage $j 'ready', 'blocked' 'rework'
            if (-not $Arg2) { Fail 'usage: rework <id> <note>' }
            # `Scope +: <glob>, <glob>` widens the spec's Scope: In; the only write to spec.md after approval.
            $widen = @($Arg2 -split '\r?\n' | Where-Object { $_ -match '^\s*Scope \+:\s*(.+)$' } |
                ForEach-Object { ($_ -replace '^\s*Scope \+:\s*', '') -split ',' } | ForEach-Object { $_.Trim().Trim('`').Trim() } | Where-Object { $_ })
            $specPath = Join-Path (JobDir $j.id) 'spec.md'
            if ($widen) {
                $lines = [Collections.Generic.List[string]]@(Get-Content $specPath)
                $scope = Read-ScopeIn @($lines)
                if (-not $scope.Globs) { Fail "rework refused: Scope + widens the spec's Scope: In globs, and this spec has none (it is not scope-checked)" }
            }
            $j.round = [int]$j.round + 1
            if ($widen) {
                $lines.InsertRange($scope.At + 1, [string[]]@($widen | ForEach-Object { "  - ``$_`` (rework $($j.round))" }))
                Set-Content $specPath $lines
                Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = "scope widened (rework $($j.round)): $($widen -join ', ')" })
            }
            Set-Content (Join-Path (JobDir $j.id) "rework-$($j.round).md") $Arg2
            Reset-Reviews $j
            $j.decision = $null
            $j.approvedAt = Now
            # The human has now decided against the new main.
            if ($j['baseMoved']) { $j.baseMoved = $null }
            if ($j.Contains('deploy') -and $j.deploy) { $j.deploy.attempts = 0 }
            $j.deployFixes = 0
            # The preview stays: the next deploy applies over it (a destroy here raced that apply, J-022).
            Move-Job $j 'approved' 'human'
        }

        'discard' {
            $j = Get-Job $Arg1
            Require-Stage $j 'backlog', 'ready', 'blocked' 'discard'
            $kids = @($Board.jobs | Where-Object { $_.Contains('base') -and $_.base -eq $j.id -and $_.stage -notin 'done', 'discarded' } | ForEach-Object id)
            if ($kids) { Fail "discard refused: $($kids -join ', ') stacked on $($j.id); discard them first, or merge $($j.id)" }
            Move-Job $j 'discarded' 'human'
            Remove-JobWork $j
            Start-Destroy $j
        }

        'purge' {
            # An unevaluated job stays: its run logs are what the evaluation is written from.
            $jobs = @($Board.jobs | Where-Object { $_.stage -in 'done', 'discarded' -and (Test-Path (Join-Path $Home_ "evals/$($_.id).json")) })
            foreach ($j in $jobs) { Remove-WorkDir $j }
            $left = @($Board.jobs | Where-Object { $_.id -notin @($jobs | ForEach-Object id) })
            $kept = @($left | ForEach-Object id)
            $ideas = @($Board.ideas | Where-Object { $_.status -in 'done', 'failed', 'abandoned' -and -not (@($_.jobs) | Where-Object { $_ -in $kept }) })
            $gone = @($ideas | ForEach-Object id) + @($jobs | ForEach-Object id)
            if (-not $gone) { return }
            Update-Seq 'J' $Board.jobs | Out-Null
            Update-Seq 'I' $Board.ideas | Out-Null
            $Board.jobs = $left
            $Board.ideas = @($Board.ideas | Where-Object { $_.id -notin $gone })
            Save-Board $Board
            foreach ($j in $jobs) { Remove-Item -Recurse -Force (JobDir $j.id) -ErrorAction SilentlyContinue }
            foreach ($i in $ideas) { Remove-Item -Recurse -Force (Join-Path $Home_ "ideas/$($i.id)") -ErrorAction SilentlyContinue }
            Add-Event ([ordered]@{ kind = 'note'; detail = "purged $($gone -join ', ')" })
            $gone
        }

        'merge' {
            $j = Get-Job $Arg1
            Require-Stage $j 'ready' 'merge'
            # The human may have merged the PR on GitHub already: that merge is the acceptance.
            $onGitHub = Get-GitHubMerge $j
            if ($onGitHub) { Complete-GitHubMerge $j $onGitHub; break }
            $b = Base-Job $j
            if ($b -and $b.stage -ne 'done') { Fail "merge refused: $($j.id) is stacked on $($b.id); merge $($b.id) first" }
            $err = Ensure-Clone $j.id
            if ($err) { Move-Job $j 'ready' 'human' 'merge-blocked' $err; Fail "merge blocked: $err" }
            $wt = WorkDir $j
            $r = Invoke-GitAuth $wt fetch -q origin
            if ($r.Code -ne 0) { Move-Job $j 'ready' 'human' 'merge-blocked' "fetch failed: $($r.Out)"; Fail 'merge blocked: fetch failed' }
            # By content, not status: an EOL-only rewrite (autocrlf + a generator) is not a change, and
            # untracked gate output is not work. safecrlf=false: the CRLF warning is not a path.
            $dirty = (Invoke-Git $wt -c core.quotepath=false -c core.safecrlf=false diff --name-only HEAD).Out
            if ($dirty) { Move-Job $j 'ready' 'human' 'merge-blocked' "uncommitted changes in the work dir: $(($dirty -split '\r?\n') -join ', ')"; Fail 'merge blocked: uncommitted changes' }
            $ahead = (Invoke-Git $wt rev-list --count "origin/main..factory/$($j.id)").Out
            if ($ahead -notmatch '^[1-9]\d*$') {
                Move-Job $j 'ready' 'human' 'merge-blocked' "nothing to merge: factory/$($j.id) adds no commits to main"
                Fail 'merge blocked: nothing to merge'
            }
            if (-not (Gate-Green $j)) {
                $g = Invoke-Gate $j.id
                $j = Get-Job $Arg1
                if ($g -ne $true) { Move-Job $j 'ready' 'gate' 'merge-blocked' $g; Fail "merge blocked: $g" }
                if (-not (Gate-Green $j)) { Move-Job $j 'ready' 'gate' 'gate-failed' 'gate red at merge; see gate.log'; Fail 'merge refused: gate red' }
            }
            $url = Publish-Job $j
            if ($url -like 'error: *') { Move-Job $j 'ready' 'human' 'merge-blocked' $url.Substring(7); Fail "merge blocked: $($url.Substring(7))" }
            # A stacked job's PR may still point at its base's branch (the base merged outside the factory).
            if ($j.Contains('base') -and $j.base) {
                $res = Invoke-GitHub PATCH "/repos/$($j.target)/pulls/$($j.pr.number)" @{ base = 'main' }
                if ($res.Status -ne 200) { Move-Job $j 'ready' 'human' 'merge-blocked' (GhError $res); Fail "merge blocked: $(GhError $res)" }
            }
            # sha pins the gated head: GitHub refuses the merge if the branch moved since.
            $res = Invoke-GitHub PUT "/repos/$($j.target)/pulls/$($j.pr.number)/merge" @{
                merge_method = 'merge'; sha = $j.gate.sha; commit_title = "Merge factory/$($j.id): $($j.title)" }
            if ($res.Status -ne 200) { Move-Job $j 'ready' 'human' 'merge-blocked' (GhError $res); Fail "merge blocked: $(GhError $res)" }
            Complete-Merge $j $res.Body.sha
        }

        'sync-prs' {
            # Ready jobs whose PR the human merged on GitHub go to done; prints each one's id.
            foreach ($j in @($Board.jobs | Where-Object { $_.stage -eq 'ready' -and $_.pr })) {
                $onGitHub = Get-GitHubMerge $j
                if ($onGitHub) { $j.id; Complete-GitHubMerge $j $onGitHub }
            }
        }

        'next' {
            Next-Gated ([bool]$NeedsCanary)
        }

        'canary' {
            Invoke-Canary ([bool]$Force) ([bool]$Full)
        }

        'checkout' {
            $j = Get-Job $Arg1
            $wt = WorkDir $j
            # The clone and fetch are a long step: the board is released around them, so a dashboard
            # move doesn't wait them out (CI: ~12 s) or, on a big repo, time out on the lock.
            Exit-BoardLock
            try {
                $err = Ensure-Clone $j.id
                # factory/base is the job's base branch as it is now, for the builder to merge in: agents
                # have no PAT to fetch with, and a branch cut before another job merged is stale (J-022).
                if (-not $err) {
                    $r = Invoke-GitAuth $wt fetch -q origin
                    if ($r.Code -ne 0) { $err = "checkout: fetch failed: $($r.Out)" }
                }
                if (-not $err) {
                    $r = Invoke-Git $wt update-ref refs/remotes/factory/base "refs/remotes/origin/$(Base-Branch $j)"
                    if ($r.Code -ne 0) { $err = "checkout: cannot set factory/base: $($r.Out)" }
                }
            } finally {
                Enter-BoardLock -1
                $script:Board = Load-Board
            }
            if ($err) { Fail $err }
            $j = Get-Job $Arg1
            # The round's start, for the provenance check: the first checkout of a build round only.
            if ($j.stage -eq 'build' -and -not $j['roundStart']) {
                $j.roundStart = (Invoke-Git $wt rev-parse HEAD).Out
                Save-Board $Board
            }
            $wt
        }

        'publish' {
            $j = Get-Job $Arg1
            Require-Stage $j 'ready', 'blocked' 'publish'
            $url = Publish-Job $j
            if ($url -like 'error: *') { Fail "publish refused: $($url.Substring(7))" }
            $url
        }

        'clone-target' {
            $err = Test-Target $Arg1
            if ($err) { Fail "clone-target refused: $err" }
            if (-not $Arg2 -or -not [IO.Path]::IsPathFullyQualified($Arg2)) { Fail 'usage: clone-target <owner/name> <absolute dir> [-Base <J-NNN>]' }
            if ($Base) {
                $err = Test-Base $Arg1 $Base
                if ($err) { Fail "base refused: $err" }
            }
            Remove-Item -LiteralPath $Arg2 -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force (Split-Path $Arg2 -Parent) | Out-Null
            $r = Invoke-GitAuth (Split-Path $Arg2 -Parent) clone -q --depth 1 --branch $(if ($Base) { "factory/$Base" } else { 'main' }) (CloneUrl $Arg1) $Arg2
            if ($r.Code -ne 0) { Fail "clone of $Arg1 failed: $($r.Out)" }
            $Arg2
        }

        'set-cost' {
            $j = Get-Job $Arg1
            $usd = [double]::Parse($Arg2, [Globalization.CultureInfo]::InvariantCulture)
            $ms = [long]$Arg3
            $j.costUsd = [Math]::Round([double]($j.costUsd ?? 0) + $usd, 4)
            $j.durationMs = [long]($j.durationMs ?? 0) + $ms
            Save-Board $Board
            Add-Event ([ordered]@{ job = $j.id; kind = 'cost'; costUsd = $usd; durationMs = $ms; provider = (Get-FactoryProvider (EnvFilePath)) })
        }

        'deploy' {
            $j = Get-Job $Arg1
            Require-Stage $j 'deploy' 'deploy'
            if (Is-Bootstrap $j) {
                # Earlier attempts' record would otherwise keep showing a failed preview.
                if ($j.Contains('deploy') -and $j.deploy) { $j.deploy.status = 'down' }
                Move-Job $j 'approve' 'orchestrator'
                Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = $BootstrapNoDeploy })
                "skipped: $BootstrapNoDeploy"
                break
            }
            if (-not (Deploys-On)) { Fail 'deploy refused: deploys are off (no HCP_ORG in .env)' }
            $d = Get-Deploy $j
            # Still applying: the last attempt's process died before it recorded a result (J-019: the
            # devops agent's shell call ended at 20 minutes). This resumes that attempt, not a new one.
            $resume = $d.status -eq 'applying'
            # The live preview's applied head; a failed or half-applied preview gets a full deploy.
            $since = if ($d.status -eq 'up' -and $d.sha) { "$($d.sha)" } else { '' }
            if (-not $resume -and [int]$d.attempts -ge $MaxDeployAttempts) { Fail "deploy refused: $($j.id) has used its $MaxDeployAttempts attempts" }
            if (-not $resume) { $d.attempts = [int]$d.attempts + 1 }
            $d.status = 'applying'
            Save-Board $Board
            $attempt = $d.attempts
            $res = Invoke-Deploy $j -Resume:$resume -Since $since
            $j = Get-Job $Arg1
            $d = $j.deploy
            $d.sha = $res.sha
            $d.run = $res.run
            if ($res.workspaceId) { $d.workspaceId = $res.workspaceId }
            if ($res.ok) {
                $d.status = 'up'
                $d.urls = $res.urls
                $d.appliedAt = Now
                $d.destroyAt = $res.destroyAt
                Save-Board $Board
                $where = if ($res.urls.frontend) { $res.urls.frontend } else { $res.urls.api }
                Add-Event ([ordered]@{ job = $j.id; kind = 'deploy'; detail = "up $where" })
                Move-Job $j 'approve' 'orchestrator'
                "frontend: $($res.urls.frontend)"
                "api: $($res.urls.api)"
            } else {
                $d.status = 'failed'
                Save-Board $Board
                Set-Content (Join-Path (JobDir $j.id) "deploy-$attempt.log") $res.log
                $first = First-Error $res.log
                Add-Event ([ordered]@{ job = $j.id; kind = 'deploy'; detail = "failed (attempt $attempt): $first" })
                if ($res.log -match $PatNeedsPattern) {
                    # Not the change's fault: give the attempt back and wait for a human to add the permission.
                    $d.attempts = [int]$d.attempts - 1
                    Move-Job $j (Escalation-Stage $j) 'orchestrator' 'github-permission' (PatNeeds $Matches[1])
                    Fail "deploy refused by GitHub: $(PatNeeds $Matches[1]); then run: factory.ps1 redeploy $($j.id)"
                }
                if ($attempt -ge $MaxDeployAttempts) { Move-Job $j (Escalation-Stage $j) 'orchestrator' 'deploy-failed' $first }
                Fail "deploy attempt $attempt failed: $first; see deploy-$attempt.log (factory.ps1 deploy-log $($j.id))"
            }
        }

        'deploy-fix' {
            # The devops agent judged the change at fault: back to the builder, with its diagnosis.
            $j = Get-Job $Arg1
            Require-FailedDeploy $j 'deploy-fix'
            if (-not $File -or -not (Test-Path $File)) { Fail "deploy-fix refused: diagnosis file not found: $File" }
            if ([int]$j['deployFixes'] -ge $MaxDeployFixes) {
                Move-Job $j (Escalation-Stage $j) 'orchestrator' 'deploy-fix-rounds' (Read-FirstLine $File)
                $j.stage
                break
            }
            $j.round = [int]$j.round + 1
            Copy-Item $File (Join-Path (JobDir $j.id) "deploy-fix-$($j.round).md") -Force
            $j.deployFixes = [int]$j['deployFixes'] + 1
            Reset-Reviews $j
            $j.deploy.attempts = 0
            Move-Job $j 'build' 'orchestrator'
            $j.stage
        }

        'block' {
            # The devops agent judged the environment or the factory at fault: no agent can fix that.
            $j = Get-Job $Arg1
            Require-FailedDeploy $j 'block'
            if (-not $Detail) { Fail 'usage: block <id> -Detail <one line>' }
            Move-Job $j (Escalation-Stage $j) 'orchestrator' 'environment' $Detail
            $j.stage
        }

        'redeploy' {
            $j = Get-Job $Arg1
            Require-Stage $j 'ready', 'blocked' 'redeploy'
            $last = @($j.history)[-1]
            # Blocked from deploy: whatever the flag, the human fixed the cause. A ready job is one that
            # left deploy before blocked existed.
            $fromDeploy = $last -and $last.from -eq 'deploy'
            $legacy = $fromDeploy -and $j.flag -and $j.flag.reason -in 'deploy-failed', 'escalated', 'github-permission'
            if ($j.stage -eq 'blocked' -and -not $fromDeploy) {
                Fail "redeploy refused: $($j.id) was blocked from $($last.from), not deploy; it never had a passing round to deploy (rework or discard it)"
            }
            if ($j.stage -eq 'ready' -and -not $legacy -and -not ($j.flag -and $j.flag.reason -eq 'github-permission')) {
                Fail "redeploy refused: $($j.id) is neither flagged github-permission nor came to ready from deploy (deploy-failed or escalated)"
            }
            # Back in the queue; the runner resumes it at deploy rather than building it again. The human
            # fixed the cause, so the attempts spent on it don't count against the change.
            $j.resume = 'deploy'
            if ($j['deploy']) { $j.deploy.attempts = 0 }
            $j.approvedAt = Now
            Move-Job $j 'approved' 'human'
        }

        'resume' {
            $j = Get-Job $Arg1
            Require-Stage $j 'ready', 'blocked' 'resume'
            if (-not ($j['flag'] -and $j.flag.reason -eq 'usage-limit')) { Fail "resume refused: $($j.id) is not flagged usage-limit (rework, redeploy or discard it)" }
            # The orchestrator continues its own session where the limit stopped it; nothing is re-run.
            $j.resume = $j.flag.stage
            $j.resumeSession = $j.flag.session
            $j.approvedAt = Now
            Move-Job $j 'approved' 'human'
        }

        'deploy-log' {
            $j = Get-Job $Arg1
            $logs = @(Get-ChildItem (JobDir $j.id) -Filter 'deploy-*.log' -ErrorAction SilentlyContinue | Sort-Object { [int]($_.BaseName -replace '\D', '') })
            if (-not $logs) { Fail "$($j.id) has no failed deploy" }
            Get-Content $logs[-1].FullName -Raw
        }

        'destroy' {
            $j = Get-Job $Arg1
            if (-not $j.Contains('deploy') -or -not $j.deploy -or $j.deploy.status -in 'down', $null) { "no preview for $($j.id)"; break }
            # A destroy that died left 'destroying'; what it had reached is unknown, so not 'up'.
            $before = if ($j.deploy.status -eq 'destroying') { 'failed' } else { $j.deploy.status }
            $j.deploy.status = 'destroying'
            $j.deploy.destroyStartedAt = Now
            Save-Board $Board
            $err = Invoke-Destroy $j
            $j = Get-Job $Arg1
            if ($err) {
                $j.deploy.status = $before
                $j.deploy.destroyError = "$err"
                $j.deploy.destroyFailures = [int]$j.deploy['destroyFailures'] + 1
                Save-Board $Board
                Add-Event ([ordered]@{ job = $j.id; kind = 'destroy'; detail = "failed: $err" })
                Fail "destroy failed: $err"
            }
            $j.deploy.status = 'down'
            $j.deploy.urls = $null
            $j.deploy.destroyError = $null
            $j.deploy.destroyFailures = 0
            Save-Board $Board
            Add-Event ([ordered]@{ job = $j.id; kind = 'destroy'; detail = 'down' })
            'down'
        }

        'sweep' {
            # Previews that outlived their work; see "Deploy" in the contract.
            if (-not (Deploys-On)) { break }
            foreach ($j in @($Board.jobs | Where-Object { $_.stage -in 'done', 'discarded' -and $_.Contains('deploy') -and $_.deploy })) {
                if (Sweep-Due $j.deploy.status $j.deploy['destroyStartedAt'] $j.deploy['destroyFailures']) {
                    $j.id
                    Start-FactoryDetached 'destroy', $j.id
                }
            }
            $c = Get-Canary
            if ($c -and $c['teardown'] -and (Sweep-Due $c.teardown.status $c.teardown.startedAt $c.teardown.failures)) {
                'canary'
                Start-FactoryDetached @('canary-destroy')
            }
        }

        'canary-destroy' {
            Invoke-CanaryTeardown
        }

        'qa-login' {
            $j = Get-Job $Arg1
            if (-not $j.Contains('deploy') -or -not $j.deploy -or $j.deploy.status -ne 'up') { Fail "qa-login refused: $($j.id) has no live preview" }
            $wsId = if ($j.deploy.Contains('workspaceId') -and $j.deploy.workspaceId) { $j.deploy.workspaceId } else { Get-HcpWorkspaceId $j.deploy.workspace }
            $outs = if ($wsId) { Get-HcpOutputs $wsId @('qa_password') } else { @{} }
            if (-not $outs['qa_user']) { Fail 'qa-login refused: the preview has no QA user (no auth add-on)' }
            "user: $($outs['qa_user'])"
            "password: $($outs['qa_password'])"
        }

        'set-stage' {
            $j = Get-Job $Arg1
            $to = $Arg2
            $by_ = if ($By) { $By } else { 'orchestrator' }
            switch ("$($j.stage)->$to") {
                { $j.stage -eq 'approved' -and $j['resumeSession'] -and $to -in 'build', 'review', 'deploy', 'approve' } {
                    # Queued by resume: back to the stage the usage limit stopped it in, as it was.
                    if (-not (Is-Next $j)) { Fail "approved->$to refused: $($j.id) is not next in the queue, or a job is already building" }
                    if ($j.resume -ne $to) { Fail "approved->$to refused: $($j.id) was resumed; it resumes at $($j.resume)" }
                    $start = $j['roundStart']
                    $j.resume = $null; $j.resumeSession = $null
                    Move-Job $j $to $by_
                    if ($start) { $j.roundStart = $start; Save-Board $Board }
                    break
                }
                'approved->build' {
                    if (-not (Is-Next $j)) { Fail "approved->build refused: $($j.id) is not next in the queue, or a job is already building" }
                    if ($j['resume']) { Fail "approved->build refused: $($j.id) was redeployed; it resumes at $($j.resume)" }
                    Reset-Reviews $j
                    Move-Job $j 'build' $by_
                }
                'approved->deploy' {
                    if (-not (Is-Next $j)) { Fail "approved->deploy refused: $($j.id) is not next in the queue, or a job is already building" }
                    if ($j['resume'] -ne 'deploy') { Fail "approved->deploy refused: $($j.id) was not redeployed" }
                    $j.resume = $null
                    Move-Job $j 'deploy' $by_
                }
                'build->review' {
                    if (-not (Test-Path (Join-Path (JobDir $j.id) "round-$($j.round)/build.md"))) { Fail "build->review refused: round-$($j.round)/build.md missing" }
                    # Build check 1, scope: the job's own changes (base work merged in excluded) against Scope: In.
                    $globs = @((Read-ScopeIn @(Get-Content (Join-Path (JobDir $j.id) 'spec.md'))).Globs)
                    $strays = @()
                    if ($globs) {
                        $wt = WorkDir $j
                        if (-not (Test-Path (Join-Path $wt '.git'))) { Fail "build->review refused: $($j.id) has no work dir to scope-check" }
                        $base = if ((Invoke-Git $wt rev-parse --verify --quiet refs/remotes/factory/base).Code -eq 0) { 'factory/base' } else { "origin/$(Base-Branch $j)" }
                        $d = Invoke-Git $wt -c core.quotePath=false diff --name-only "$base...HEAD"
                        if ($d.Code -ne 0) { Fail "build->review refused: cannot diff $base...HEAD: $($d.Out)" }
                        $res = @($globs | ForEach-Object { Glob-Regex $_ })
                        $strays = @($d.Out -split '\r?\n' | Where-Object { $_ } | Where-Object { $p = $_; -not ($res | Where-Object { $p -match $_ }) })
                    }
                    # Build check 2, base moved: build.md must say whether the spec holds against the new main.
                    $stale = $null
                    if (-not $strays -and $j['baseMoved']) {
                        $section = Read-Section (Join-Path (JobDir $j.id) "round-$($j.round)/build.md") 'Spec against base'
                        $holds = if ($section -match '(?m)^\s*Spec holds:\s*(yes|no)\b') { $Matches[1] }
                        if (-not $holds) { Fail "build->review refused: $($j.id)'s base moved ($($j.baseMoved.by) merged), and round-$($j.round)/build.md has no '## Spec against base' with a 'Spec holds: yes|no' line" 2 }
                        if ($holds -eq 'yes') { $j.baseMoved = $null } else { $stale = "## Spec against base`n$section".TrimEnd() }
                    }
                    # Build check 3, template provenance (rounds after 1): build.md's Template gaps names each path.
                    if (-not $strays -and -not $stale -and [int]$j.round -gt 1) {
                        $prov = Get-Provenance $j
                        if ($null -eq $prov.Paths) {
                            Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = "not provenance-checked: $($prov.Note)" })
                        } else {
                            $gaps = "$(Read-Section (Join-Path (JobDir $j.id) "round-$($j.round)/build.md") 'Template gaps')"
                            $unnamed = @($prov.Paths | Where-Object { -not $gaps.Contains($_) })
                            if ($unnamed) { Fail "build->review refused: round $($j.round) changed template-sourced paths that build.md's '## Template gaps' does not name (as a gap, or '<path>: job-specific, <why>'): $($unnamed -join ', ')" 2 }
                        }
                    }
                    if ($strays) {
                        $n = $strays.Count
                        $detail = "$n path$(if ($n -ne 1) { 's' }) outside Scope: In: $(($strays | Select-Object -First 3) -join ', ')$(if ($n -gt 3) { ', ...' })"
                        $why = "Paths outside the spec's Scope: In:`n" + (($strays | ForEach-Object { "- ``$_``" }) -join "`n")
                        Move-Job $j (Escalation-Stage $j) $by_ 'out-of-scope' $detail $why
                    } elseif ($stale) {
                        Move-Job $j (Escalation-Stage $j) $by_ 'spec-stale' "the spec does not hold since $($j.baseMoved.by) merged" $stale
                    } else {
                        $j.reviews = [ordered]@{ security = 'running'; code = 'running'; ux = 'running'; ui = 'running' }
                        Move-Job $j 'review' $by_
                        if (-not $globs) { Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = 'not scope-checked: the spec has no Scope: In globs' }) }
                    }
                    $j.stage
                }
                default {
                    if ($to -eq 'ready' -and $j.stage -in 'build', 'review', 'deploy', 'approve' -and $Reason -eq 'usage-limit') {
                        if (-not $Session) { Fail "usage-limit refused: $($j.id) needs -Session, the claude session to resume" }
                        $from = $j.stage
                        Move-Job $j (Escalation-Stage $j) $by_ 'usage-limit' $Detail
                        $j.flag.stage = $from
                        $j.flag.session = $Session
                        Save-Board $Board
                    } elseif ($to -eq 'ready' -and $j.stage -in $Building -and $Reason -eq 'escalated') {
                        Move-Job $j (Escalation-Stage $j) $by_ 'escalated' $Detail
                    } else {
                        Fail "transition $($j.stage)->$to refused (use the dedicated command, or see FACTORY-CONTRACT.md)"
                    }
                }
            }
        }

        'set-review' {
            $j = Get-Job $Arg1
            if ($Arg2 -notin $Roles) { Fail "unknown role $Arg2" }
            if ($Arg3 -notin 'pending', 'running', 'PASS', 'CHANGES') { Fail "unknown review state $Arg3" }
            $j.reviews[$Arg2] = $Arg3
            $j.updatedAt = Now
            Save-Board $Board
        }

        'read-reviews' {
            $j = Get-Job $Arg1
            Require-Stage $j 'review' 'read-reviews'
            $dir = Join-Path (JobDir $j.id) "round-$($j.round)"
            $missing = @()
            foreach ($role in $Roles) {
                $line = Read-FirstLine (Join-Path $dir "review-$role.md")
                if ($line -ceq 'Verdict: PASS') { $j.reviews[$role] = 'PASS' }
                elseif ($line -ceq 'Verdict: CHANGES') { $j.reviews[$role] = 'CHANGES' }
                else { $missing += $role }
            }
            if ($missing) { Save-Board $Board; Fail "no verdict from: $($missing -join ', ')" 2 }
            $changes = @($Roles | Where-Object { $j.reviews[$_] -eq 'CHANGES' })
            if (-not $changes) {
                if (Is-Bootstrap $j) {
                    Move-Job $j 'approve' 'orchestrator'
                    Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = $BootstrapNoDeploy })
                }
                elseif (Deploys-On) { Move-Job $j 'deploy' 'orchestrator' }
                else {
                    Move-Job $j 'approve' 'orchestrator'
                    Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = 'deploy skipped: no HCP_ORG in .env' })
                }
            }
            elseif ([int]$j.round -lt 2) {
                $j.round = [int]$j.round + 1
                Reset-Reviews $j
                Move-Job $j 'build' 'orchestrator'
            }
            else { Move-Job $j (Escalation-Stage $j) 'orchestrator' 'max-rounds' "CHANGES from $($changes -join ', ') at round $($j.round)" }
            $j.stage
        }

        'read-decision' {
            $j = Get-Job $Arg1
            Require-Stage $j 'approve' 'read-decision'
            $path = Join-Path (JobDir $j.id) 'decision.md'
            $line = Read-FirstLine $path
            if ($line -ceq 'Decision: APPROVE') { $j.decision = 'APPROVE'; Move-Job $j 'gate' 'orchestrator' }
            elseif ($line -ceq 'Decision: ESCALATE') {
                $j.decision = 'ESCALATE'
                $why = Read-Section $path 'Escalation reason'
                $detail = if ($why) { ($why -split "`n")[0].Trim() } else { 'approver escalated; see decision.md' }
                Move-Job $j 'ready' 'orchestrator' 'escalated' $detail $why
            }
            else { Fail 'no decision line in decision.md' 2 }
            $j.stage
        }

        'provenance' {
            $j = Get-Job $Arg1
            $p = Get-Provenance $j
            if ($p.Note) { [Console]::Error.WriteLine("factory: note: $($p.Note)") }
            $p.Paths
        }

        'gate-check' {
            $j = Get-Job $Arg1
            Require-Stage $j 'build', 'review', 'deploy', 'approve' 'gate-check'
            $err = Ensure-Clone $j.id
            if ($err) { Fail "gate-check refused: $err" }
            $cmds = @(Gate-Commands $j)
            if ($Arg2) { $cmds = @($cmds | Where-Object { $_.name -eq $Arg2 }) }
            if (-not $cmds) { Fail "gate-check refused: $(if ($Arg2) { "no gate command named '$Arg2'" } else { 'no gate commands in the spec' })" }
            $wt = WorkDir $j
            $dir = JobDir $j.id
            New-Item -ItemType Directory -Force $dir | Out-Null
            $cachePath = Join-Path $dir 'gate-check.json'
            $lines = [Collections.Generic.List[string]]::new()
            $first = 0
            Exit-BoardLock
            try {
                $lock = Enter-GateCheckLock (Join-Path $dir 'gate-check.lock')
                if (-not $lock) { Fail "gate-check refused: another gate-check held $($j.id)'s lock for too long" }
                try {
                    $sha = (Invoke-Git $wt rev-parse HEAD).Out
                    $clean = -not (Invoke-Git $wt status --porcelain).Out
                    $cache = if (Test-Path $cachePath) { Get-Content $cachePath -Raw | ConvertFrom-Json -AsHashtable } else { $null }
                    if (-not $cache -or $cache.sha -ne $sha) { $cache = @{ sha = $sha; results = @{} } }
                    $lines.Add("# gate-check $($j.id) at $(Now)")
                    foreach ($c in $cmds) {
                        $hit = if ($clean -and $cache.results.ContainsKey($c.name)) { $cache.results[$c.name] }
                        $r = if ($hit) { [pscustomobject]@{ Code = [int]$hit.code; Out = $hit.out } } else { Invoke-GateCommand $c $wt }
                        if (-not $hit -and $clean) { $cache.results[$c.name] = @{ code = $r.Code; out = $r.Out } }
                        $lines.Add("`n## $($c.name): $($c.cmd)")
                        $lines.Add($r.Out.TrimEnd())
                        $lines.Add("exit $($r.Code)$(if ($hit) { " (cached at $($sha.Substring(0, [Math]::Min(7, $sha.Length))))" })")
                        if ($r.Code -ne 0 -and $first -eq 0) { $first = $r.Code }
                    }
                    if ($clean) {
                        $tmp = "$cachePath.$PID.tmp"
                        [IO.File]::WriteAllText($tmp, ($cache | ConvertTo-Json -Depth 5))
                        Replace-File $tmp $cachePath
                    }
                    Set-Content (Join-Path $dir 'gate-check.log') $lines
                } finally { $lock.Dispose() }
            } finally {
                Enter-BoardLock -1
                $script:Board = Load-Board
            }
            $lines
            if ($first -ne 0) { exit $first }
        }

        'gate' {
            $j = Get-Job $Arg1
            Require-Stage $j 'gate' 'gate'
            $g = Invoke-Gate $j.id
            $j = Get-Job $Arg1
            if ($g -ne $true) { Move-Job $j 'ready' 'gate' 'escalated' $g; Fail $g }
            if (Gate-Green $j) { Move-Job $j 'ready' 'gate' }
            else { Move-Job $j 'ready' 'gate' 'gate-failed' 'a gate command exited non-zero; see gate.log'; exit 1 }
        }

        'event' {
            if ($Arg1) {
                $e = $Arg1 | ConvertFrom-Json -AsHashtable
            } else {
                $e = [ordered]@{}
                if ($Job) { $e.job = $Job }
                if ($Idea) { $e.idea = $Idea }
                $e.kind = $Kind
                if ($Agent) { $e.agent = $Agent }
                if ($Detail) { $e.detail = $Detail }
            }
            if ($e.kind -notin $EventKinds) { Fail "unknown event kind '$($e.kind)'" }
            if ($e.Contains('job') -and $e.job -and -not $e.Contains('round')) {
                $j = $Board.jobs | Where-Object { $_.id -eq $e.job } | Select-Object -First 1
                if ($j) { $e.round = $j.round }
            }
            Add-Event $e
            Copy-Transcript $e
        }

        'eval' {
            $j = Get-Job $Arg1
            Require-Stage $j 'ready', 'done', 'discarded' 'eval'
            if (-not $File -or -not (Test-Path $File)) { Fail "eval file not found: $File" }
            $line = Read-FirstLine $File
            if ($line -cnotmatch '^Rating: (good|mixed|poor)$') { Fail "no 'Rating: good|mixed|poor' first line in $File" 2 }
            $rating = $Matches[1]
            $gate = if (-not $j.gate) { $null }
                    elseif (@($j.gate.Keys | Where-Object { $_ -notin 'sha', 'at' -and $j.gate[$_] -ne 0 })) { 'red' } else { 'green' }
            $dir = Join-Path $Home_ 'evals'
            New-Item -ItemType Directory -Force $dir | Out-Null
            Copy-Item $File (Join-Path $dir "$($j.id).md") -Force
            $snap = [ordered]@{
                job = $j.id; idea = $j.idea; title = $j.title; kind = $j.kind ?? 'feature'; target = $j.target
                rating = $rating; stage = $j.stage; round = $j.round; flag = $j.flag ? $j.flag.reason : $null
                reviews = $j.reviews; decision = $j.decision; gate = $gate; merged = $j.merged ? $j.merged.sha : $null
                costUsd = $j.costUsd; durationMs = $j.durationMs; evaluatedAt = Now
                models = [ordered]@{}; agents = @(); gateMs = $null
            }
            foreach ($run in Get-ChildItem (JobDir $j.id) -Filter 'run-*.jsonl' -File -ErrorAction SilentlyContinue) {
                # Each result carries the session's usage so far, so a run woken again by a background
                # task logs several; the last is the run's total, as the runner records it.
                foreach ($l in Get-Content $run.FullName | Where-Object { $_ -match '"type"\s*:\s*"result"' } | Select-Object -Last 1) {
                    try { $r = $l | ConvertFrom-Json -AsHashtable } catch { continue }
                    if (-not $r['modelUsage']) { continue }
                    foreach ($m in @($r['modelUsage'].Keys)) {
                        $u = $r['modelUsage'][$m]
                        $acc = $snap.models[$m] ?? ($snap.models[$m] = [ordered]@{ costUsd = 0.0; outputTokens = 0 })
                        $acc.costUsd = [Math]::Round($acc.costUsd + [double]($u['costUSD'] ?? 0), 4)
                        $acc.outputTokens += [long]($u['outputTokens'] ?? 0)
                    }
                }
            }
            $ms = { param($a, $b) [long]([DateTimeOffset]::Parse($b) - [DateTimeOffset]::Parse($a)).TotalMilliseconds }
            $open = @{}; $agents = [Collections.Generic.List[object]]::new(); $gateAt = $null
            foreach ($l in Get-Content $EventsPath -ErrorAction SilentlyContinue) {
                try { $e = $l | ConvertFrom-Json -AsHashtable -DateKind String } catch { continue }
                if ($e['job'] -ne $j.id) { continue }
                $agent = $e['agent']; $round = $e['round']
                $key = "$agent|$round"
                switch ($e.kind) {
                    'agent-start' { $open[$key] = $e.ts }
                    'agent-stop' {
                        if (-not $open.ContainsKey($key)) { break }
                        $a = $agents | Where-Object { $_.agent -eq $agent -and $_.round -eq $round } | Select-Object -First 1
                        if (-not $a) { $a = [ordered]@{ agent = $agent; round = $round; ms = 0L }; $agents.Add($a) }
                        $a.ms += & $ms $open[$key] $e.ts
                        $open.Remove($key)
                    }
                    'stage' { if ($e['to'] -eq 'gate') { $gateAt = $e.ts } }
                    'gate' { if ($gateAt) { $snap.gateMs = [long]($snap.gateMs ?? 0) + (& $ms $gateAt $e.ts); $gateAt = $null } }
                }
            }
            $snap.agents = @($agents)
            $prev = Join-Path $dir "$($j.id).json"
            if (Test-Path $prev) { $h = (Get-Content $prev -Raw | ConvertFrom-Json -AsHashtable -DateKind String)['human']; if ($h) { $snap.human = $h } }
            $tmp = Join-Path $dir "$($j.id).json.$PID.tmp"
            [IO.File]::WriteAllText($tmp, ($snap | ConvertTo-Json -Depth 10))
            Replace-File $tmp (Join-Path $dir "$($j.id).json")
            Add-Event ([ordered]@{ job = $j.id; kind = 'eval'; detail = $rating })
        }

        'feedback' {
            if ($Arg1 -notmatch '^[A-Z]+-\d+$') { Fail "feedback refused: '$Arg1' is not a job id" }
            $dir = Join-Path $Home_ 'evals'
            $path = Join-Path $dir "$Arg1.json"
            if (-not (Test-Path $path)) { Fail "feedback refused: $Arg1 has no evaluation" }
            if (-not $File -or -not (Test-Path $File)) { Fail "feedback file not found: $File" }
            if ((Read-FirstLine $File) -cnotmatch '^Rating: (good|mixed|poor)$') { Fail "no 'Rating: good|mixed|poor' first line in $File" 2 }
            $rating = $Matches[1]
            $snap = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable -DateKind String
            $snap.human = [ordered]@{ rating = $rating; at = Now }
            Copy-Item $File (Join-Path $dir "$Arg1.feedback.md") -Force
            $tmp = "$path.$PID.tmp"
            [IO.File]::WriteAllText($tmp, ($snap | ConvertTo-Json -Depth 10))
            Replace-File $tmp $path
            Add-Event ([ordered]@{ job = $Arg1; kind = 'feedback'; detail = $rating })
        }

        'lesson' {
            $j = Get-Job $Arg1
            if ($File -and -not (Test-Path $File)) { Fail "lesson file not found: $File" }
            $gaps = @()
            foreach ($d in Get-ChildItem (JobDir $j.id) -Directory -Filter 'round-*' -ErrorAction SilentlyContinue | Sort-Object { [int]($_.Name -replace '\D', '') }) {
                $g = Read-Section (Join-Path $d.FullName 'deploy.md') 'Template gaps'
                # "None", written bare or as a bullet ("- None."), is no gap.
                if ($g -and $g.Trim() -notmatch '^(?i)(-\s*)?none\.?$') { $gaps += [pscustomobject]@{ round = $d.Name; text = $g } }
                # The builder's too, without the paths it judged job-specific (Build checks, template provenance).
                $g = Read-Section (Join-Path $d.FullName 'build.md') 'Template gaps'
                if ($g) { $g = (($g -split "`n") | Where-Object { $_ -notmatch '^- [^\s]+: job-specific\b' }) -join "`n" }
                if ($g -and $g.Trim() -and $g.Trim() -notmatch '^(?i)(-\s*)?none\.?$') { $gaps += [pscustomobject]@{ round = "$($d.Name), build"; text = $g.Trim() } }
            }
            $count = 0
            foreach ($g in $gaps) { $count += @($g.text -split "`n" | Where-Object { $_ -match '^- ' }).Count }
            if (-not $j.flag -and -not $gaps -and -not $File) { 'no lessons'; break }

            $md = @("# Lessons: $($j.id) - $($j.title)", '', "- Target: $($j.target)")
            if ($j.flag) {
                $md += "- Flag: $($j.flag.reason): $($j.flag.detail)"
                if ($j.flag.Contains('why') -and $j.flag.why) { $md += '', '## Why it was flagged', $j.flag.why }
            }
            foreach ($g in $gaps) { $md += '', "## Template gaps ($($g.round))", $g.text }
            if ($File) { $md += '', '## From the evaluation', (Get-Content $File -Raw).Trim() }

            $dir = Join-Path $Home_ 'lessons'
            New-Item -ItemType Directory -Force $dir | Out-Null
            $path = Join-Path $dir "$($j.id).json"
            $old = if (Test-Path $path) { Get-Content $path -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
            $info = [ordered]@{ job = $j.id; title = $j.title; target = $j.target; flag = $(if ($j.flag) { $j.flag.reason })
                templateGaps = $count; status = $(if ($old) { $old.status } else { 'open' }); session = $(if ($old) { $old.session }); at = Now }
            Set-Content (Join-Path $dir "$($j.id).md") ($md -join "`n")
            $tmp = "$path.$PID.tmp"
            [IO.File]::WriteAllText($tmp, ($info | ConvertTo-Json -Depth 5))
            Replace-File $tmp $path
            Add-Event ([ordered]@{ job = $j.id; kind = 'lesson'; detail = "$count" })
            "lessons/$($j.id).md"
        }

        'lesson-mark' {
            if ($Arg1 -notmatch '^[A-Z]+-\d+$') { Fail "lesson-mark refused: '$Arg1' is not a job id" }
            if ($Arg2 -notin 'open', 'proposed', 'applied') { Fail 'lesson-mark: status must be open, proposed or applied' }
            $path = Join-Path (Join-Path $Home_ 'lessons') "$Arg1.json"
            if (-not (Test-Path $path)) { Fail "lesson-mark refused: $Arg1 has no lessons" }
            $info = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable -DateKind String
            $info.status = $Arg2
            if ($Detail) { $info.session = $Detail }
            $tmp = "$path.$PID.tmp"
            [IO.File]::WriteAllText($tmp, ($info | ConvertTo-Json -Depth 5))
            Replace-File $tmp $path
        }

        'status' {
            if ($Json) { return ($Board | ConvertTo-Json -Depth 20) }
            if (-not $Board.jobs -and -not $Board.ideas) { return 'board is empty' }
            foreach ($i in $Board.ideas | Where-Object { $_.status -ne 'done' }) { '{0,-6} {1,-10} {2}' -f $i.id, "idea:$($i.status)", $i.text }
            foreach ($j in $Board.jobs | Where-Object { $_.stage -ne 'discarded' }) {
                $flag = if ($j.flag) { " [$($j.flag.reason)]" } else { '' }
                '{0,-6} {1,-10} r{2} {3}{4}' -f $j.id, $j.stage, $j.round, $j.title, $flag
            }
        }

        'state-migrate' {
            if (-not (State-IsAzure)) { Fail 'state-migrate refused: FACTORY_STATE is not azure' }
            try { $n = Invoke-StateMigrate } catch { Fail "$($_.Exception.Message)" }
            "migrated: $n blobs to $($script:StateUrl)"
        }

        'state-pull' {
            if (-not (State-IsAzure)) { Fail 'state-pull refused: FACTORY_STATE is not azure' }
            try { $n = Pull-State $Arg1; $null = Sync-Events } catch { Fail "$($_.Exception.Message)" }
            "pulled: $n"
        }

        'prune' {
            if (-not (State-IsAzure)) { Fail 'prune refused: FACTORY_STATE is not azure' }
            try { $r = Invoke-StatePrune -Jobs $Board.jobs } catch { Fail "$($_.Exception.Message)" }
            if ($r.Deleted) { Add-Event ([ordered]@{ kind = 'note'; detail = "pruned $($r.Deleted) blobs, $($r.Freed) bytes freed" }) }
            "$($r.Used) of $($r.Cap) bytes"
        }

        'allow-ip' {
            if (-not (State-IsAzure)) { Fail 'allow-ip refused: FACTORY_STATE is not azure' }
            try { $r = Invoke-StateAllowIp } catch { Fail "$($_.Exception.Message)" }
            "allowed $($r.Ip) on $($r.Account)" + $(if ($r.Removed) { "; removed $($r.Removed)" } else { '' })
        }

        default { Fail "unknown command '$Command'" }
    }
}
finally {
    if (State-IsAzure) {
        try { Sync-Up } catch { [Console]::Error.WriteLine("factory: state push failed: $($_.Exception.Message)") }
    }
    Exit-BoardLease
    $Mutex.ReleaseMutex()
    $Mutex.Dispose()
}

exit 0
