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
    [switch] $Failed,
    [switch] $Json
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$Roles = 'security', 'code', 'ux', 'ui'
$Building = 'build', 'review', 'approve', 'gate'
$EventKinds = 'idea', 'stage', 'agent-start', 'agent-stop', 'gate', 'merge', 'cost', 'note', 'eval', 'feedback'

function Fail([string] $msg, [int] $code = 1) {
    [Console]::Error.WriteLine("factory: $msg")
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

# The repo a job builds in and merges to: its target, or this repo for legacy jobs with none.
function JobRoot($j) {
    if ($j -and $j.Contains('target') -and $j.target) { return $j.target }
    MainRoot
}

function WorktreePath($j) {
    if ($env:FACTORY_WORKTREE) { return $env:FACTORY_WORKTREE }
    (JobRoot $j).TrimEnd('\', '/') + '.wt'
}

# Returns $null for a usable target ('repo' or 'new'), or why it is refused.
function Test-Target([string] $path) {
    if (-not $path) { return 'a target repo path is required' }
    if (-not [IO.Path]::IsPathFullyQualified($path)) { return "target '$path' is not an absolute path" }
    if (-not (Test-Path $path)) { return $null }
    if (-not (Test-Path $path -PathType Container)) { return "target '$path' is a file" }
    if (Test-Path (Join-Path $path '.git')) { return $null }
    if (@(Get-ChildItem $path -Force).Count -eq 0) { return $null }
    "target '$path' is a non-empty folder that is not a git repo"
}

# A new target gets scaffolded by a bootstrap job: it does not exist yet, or is an empty folder.
function Test-NewTarget([string] $path) {
    -not (Test-Path $path) -or (-not (Test-Path (Join-Path $path '.git')) -and @(Get-ChildItem $path -Force).Count -eq 0)
}

# Returns $null for valid scaffold options (see "Targets" in the contract), or why they are refused.
function Test-Scaffold($sc) {
    if ($sc -isnot [Collections.IDictionary]) { return 'the scaffold options must be a JSON object' }
    if ("$($sc.name)" -cnotmatch '^[a-z][a-z0-9]*$') { return "scaffold name '$($sc.name)' must be lower-case letters and digits, starting with a letter" }
    if ("$($sc.resource)" -cnotmatch '^[A-Z][A-Za-z0-9]*$') { return "scaffold resource '$($sc.resource)' must be PascalCase" }
    if ($sc.db -cnotin 'sqlite', 'sqlserver', 'postgres') { return "scaffold db '$($sc.db)' must be sqlite, sqlserver or postgres" }
    if ($sc.ui -cnotin 'react', 'blazor', 'blazor-maui', 'none') { return "scaffold ui '$($sc.ui)' must be react, blazor, blazor-maui or none" }
    if ($sc.Contains('events') -and $sc.events -isnot [bool]) { return 'scaffold events must be true or false' }
    $null
}

function TemplatesRoot {
    if ($env:FACTORY_TEMPLATES) { return $env:FACTORY_TEMPLATES }
    Join-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) 'template-projects'
}

# The bootstrap job's spec: factory/templates/bootstrap.md with the human's scaffold options filled in.
function Bootstrap-Spec($sc, [string] $target) {
    $n = $sc.name; $r = $sc.resource
    $cmds = @("dotnet new aspire-base -n $n -o . --dbProvider $($sc.db) --resource $r")
    switch ($sc.ui) {
        'react' { $cmds += "dotnet new aspire-ui-react -n $n -o . --resource $r" }
        'blazor' { $cmds += "dotnet new aspire-ui-blazor -n $n -o . --web true --maui false --resource $r" }
        'blazor-maui' { $cmds += "dotnet new aspire-ui-blazor -n $n -o . --web true --maui true --resource $r" }
    }
    if ($sc.events) {
        $store = @{ sqlite = 'fisher'; sqlserver = 'polecat'; postgres = 'marten' }[$sc.db]
        $cmds += "dotnet new aspire-events -n $n -o . --eventStore $store --resource $r"
    }
    $gate = @("- build: ``dotnet build $n.slnx``", "- test: ``dotnet test $n.slnx``")
    if ($sc.ui -eq 'react') { $gate += '- ui: `Set-Location src/ui/react; if (-not (Test-Path node_modules/.package-lock.json) -or (Get-Item package-lock.json).LastWriteTime -gt (Get-Item node_modules/.package-lock.json).LastWriteTime) { npm ci }; npm run build`' }
    $text = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'templates/bootstrap.md') -Raw
    $values = @{
        name = $n; resource = $r; db = $sc.db; ui = $sc.ui; events = "$([bool]$sc.events)".ToLowerInvariant()
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

function JobDir([string] $id) { Join-Path $Home_ "jobs/$id" }

# --- board I/O (serialized across processes, atomic write) ------------------------------------

$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Home_.ToLowerInvariant()))).Substring(0, 16)
$Mutex = [Threading.Mutex]::new($false, "factory-board-$hash")
try { $null = $Mutex.WaitOne([TimeSpan]::FromSeconds(30)) } catch [Threading.AbandonedMutexException] { }

function Load-Board {
    if (-not (Test-Path $BoardPath)) { return @{ version = 2; ideas = @(); jobs = @() } }
    $b = Get-Content $BoardPath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    if (-not $b.ContainsKey('ideas')) { $b.ideas = @() }
    $b
}

function Save-Board($b) {
    $tmp = "$BoardPath.$PID.tmp"
    [IO.File]::WriteAllText($tmp, ($b | ConvertTo-Json -Depth 20))
    [IO.File]::Move($tmp, $BoardPath, $true)
}

function Add-Event([hashtable] $e) {
    $o = [ordered]@{ ts = Now }
    foreach ($k in $e.Keys) { $o[$k] = $e[$k] }
    [IO.File]::AppendAllText($EventsPath, ($o | ConvertTo-Json -Compress -Depth 10) + "`n")
}

$Board = Load-Board

function Get-Job([string] $id) {
    $j = $Board.jobs | Where-Object { $_.id -eq $id } | Select-Object -First 1
    if (-not $j) { Fail "no job $id" }
    $j
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
        target = (Read-SpecMeta $spec).repo
        reviews = $null; decision = $null; gate = $null; flag = $null; merged = $null; approvedAt = $null
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

function Move-Job($j, [string] $to, [string] $by, [string] $reason = $null, [string] $detail = $null) {
    $from = $j.stage
    $ts = Now
    $j.stage = $to
    $j.updatedAt = $ts
    $j.flag = if ($reason) { [ordered]@{ reason = $reason; detail = $detail } } else { $null }
    $j.history = @($j.history) + [ordered]@{ ts = $ts; from = $from; to = $to; by = $by }
    Save-Board $Board
    $e = [ordered]@{ job = $j.id; kind = 'stage'; from = $from; to = $to; by = $by }
    if ($reason) { $e.reason = $reason }
    Add-Event $e
}

function Require-Stage($j, [string[]] $stages, [string] $what) {
    if ($j.stage -notin $stages) { Fail "$what refused: $($j.id) is in '$($j.stage)', needs $($stages -join '|')" }
}

function Read-FirstLine([string] $path) {
    if (-not (Test-Path $path)) { return $null }
    $line = Get-Content $path -TotalCount 1
    if ($null -eq $line) { return $null }
    $line.TrimEnd()
}

function Next-Runnable {
    if ($Board.jobs | Where-Object { $_.stage -in $Building }) { return $null }
    $Board.jobs | Where-Object { $_.stage -eq 'approved' } |
        Sort-Object { $_.approvedAt }, { $_.id } | Select-Object -First 1
}

# --- git helpers ----------------------------------------------------------------------------

function Invoke-Git([string] $Where) {
    $out = & git -C $Where @args 2>&1
    [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($out | Out-String).Trim() }
}

function Branch-Exists([string] $root, [string] $branch) { (Invoke-Git $root rev-parse --verify --quiet "refs/heads/$branch").Code -eq 0 }

# Deletes factory/<id> in the job's repo, detaching its worktree first if it is on that branch.
function Remove-JobBranch($j) {
    $branch = "factory/$($j.id)"
    $root = JobRoot $j
    if (-not (Test-Path (Join-Path $root '.git')) -or -not (Branch-Exists $root $branch)) { return }
    $wt = WorktreePath $j
    if ((Test-Path $wt) -and (Invoke-Git $wt branch --show-current).Out -eq $branch) {
        $r = Invoke-Git $wt checkout -q --detach
        if ($r.Code -ne 0) { Fail "cannot detach worktree: $($r.Out)" }
    }
    $r = Invoke-Git $root branch -D $branch
    if ($r.Code -ne 0) { Fail "cannot delete $($branch): $($r.Out)" }
}

# Creates a missing (or empty) target as a git repo with an empty initial commit on main.
function Ensure-Repo([string] $root) {
    $err = Test-Target $root
    if ($err) { return $err }
    if (Test-Path (Join-Path $root '.git')) { return $null }
    New-Item -ItemType Directory -Force $root | Out-Null
    $r = Invoke-Git $root init -q -b main
    if ($r.Code -ne 0) { return "git init failed: $($r.Out)" }
    $r = Invoke-Git $root -c user.name=factory -c user.email=factory@localhost commit -q --allow-empty -m 'Initial commit (factory)'
    if ($r.Code -ne 0) { return "initial commit failed: $($r.Out)" }
    $null
}

function Ensure-Worktree([string] $id) {
    $j = Get-Job $id
    $root = JobRoot $j
    $err = Ensure-Repo $root
    if ($err) { return $err }
    $wt = WorktreePath $j
    $branch = "factory/$id"
    if (-not (Test-Path $wt)) {
        $r = Invoke-Git $root worktree add --detach $wt main
        if ($r.Code -ne 0) { return "worktree add failed: $($r.Out)" }
    }
    $current = (Invoke-Git $wt branch --show-current).Out
    if ($current -eq $branch) { return $null }
    if ((Invoke-Git $wt status --porcelain).Out) { return "worktree $wt has uncommitted changes on '$current'" }
    $r = if (Branch-Exists $root $branch) { Invoke-Git $wt checkout -q $branch } else { Invoke-Git $wt checkout -q -b $branch main }
    if ($r.Code -ne 0) { return "checkout $branch failed: $($r.Out)" }
    $null
}

function Gate-Commands($j) {
    if ($env:FACTORY_GATE_CMDS) { return @($env:FACTORY_GATE_CMDS | ConvertFrom-Json) }
    $gate = (Read-SpecMeta (Join-Path (JobDir $j.id) 'spec.md')).gate
    if ($gate) { return $gate }
    if ($j.Contains('target') -and $j.target) { return @() }
    @(
        [pscustomobject]@{ name = 'build'; cmd = 'dotnet build shelf.slnx' }
        [pscustomobject]@{ name = 'test'; cmd = 'dotnet test tests/shelf.api.tests' }
        [pscustomobject]@{ name = 'ui'; cmd = 'Set-Location src/ui/react; if (-not (Test-Path node_modules)) { npm ci }; npm run build' }
    )
}

# Runs the gate in the worktree on factory/<id> and records exit codes and sha on the job.
# Returns $true when it ran (green or red), or an error string. The board lock is released while
# the commands run, so callers must re-fetch the job afterwards.
function Invoke-Gate([string] $id) {
    $err = Ensure-Worktree $id
    if ($err) { return $err }
    $j = Get-Job $id
    $wt = WorktreePath $j
    $cmds = @(Gate-Commands $j)
    if (-not $cmds) { return 'no gate commands in the spec' }
    $log = Join-Path (JobDir $j.id) 'gate.log'
    New-Item -ItemType Directory -Force (JobDir $j.id) | Out-Null
    Set-Content $log "# gate $($j.id) at $(Now)"
    $result = [ordered]@{}
    $Mutex.ReleaseMutex()
    # npm and vite write UTF-8; a runner started by serve.ps1 has a fresh console on the OEM codepage.
    $enc = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
    foreach ($c in $cmds) {
        Add-Content $log "`n## $($c.name): $($c.cmd)"
        Push-Location $wt
        try {
            $out = & pwsh -NoProfile -Command $c.cmd 2>&1
            $code = $LASTEXITCODE
        } finally { Pop-Location }
        Add-Content $log ($out | Out-String)
        Add-Content $log "exit $code"
        $result[$c.name] = $code
    }
    } finally {
        [Console]::OutputEncoding = $enc
        $null = $Mutex.WaitOne()
        $script:Board = Load-Board
    }
    $j = Get-Job $id
    $result.sha = (Invoke-Git (JobRoot $j) rev-parse "factory/$($j.id)").Out
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
    $head = (Invoke-Git (JobRoot $j) rev-parse "factory/$($j.id)").Out
    $j.gate.sha -eq $head
}

# --- commands -------------------------------------------------------------------------------

try {
    switch ($Command) {

        'new-idea' {
            if (-not $Arg1) { Fail 'usage: new-idea <text> -Target <path> [-Scaffold <json>]' }
            $err = Test-Target $Target
            if ($err) { Fail "new-idea refused: $err" }
            $new = Test-NewTarget $Target
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
            Set-Content (Join-Path $dir 'idea.md') @($Arg1, '', "Target: $Target")
            $i = [ordered]@{ id = $id; text = $Arg1; target = $Target; scaffold = $sc; status = $status; jobs = @(); createdAt = Now }
            $Board.ideas = @($Board.ideas) + $i
            Save-Board $Board
            Add-Event ([ordered]@{ idea = $id; kind = 'idea'; detail = $status })
            if ($new) { "$id $(Add-Job $i (Bootstrap-Spec $sc $Target) 'the bootstrap template' 'bootstrap')" } else { $id }
        }

        'check-target' {
            $err = Test-Target $Arg1
            if ($err) { Fail "check-target refused: $err" }
            if (Test-NewTarget $Arg1) { 'new' } else { 'existing' }
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
            if (-not $meta.repo) { Fail "approve refused: the spec has no '## Target' with '- Repo: <path>'" }
            $err = Test-Target $meta.repo
            if ($err) { Fail "approve refused: $err" }
            if (-not $meta.gate) { Fail "approve refused: the spec's '## Gate' has no '- <name>: ``<command>``' lines" }
            $j.target = $meta.repo
            $j.approvedAt = Now
            Move-Job $j 'approved' 'human'
        }

        'rework' {
            $j = Get-Job $Arg1
            Require-Stage $j 'ready' 'rework'
            if (-not $Arg2) { Fail 'usage: rework <id> <note>' }
            $j.round = [int]$j.round + 1
            Set-Content (Join-Path (JobDir $j.id) "rework-$($j.round).md") $Arg2
            Reset-Reviews $j
            $j.decision = $null
            $j.approvedAt = Now
            Move-Job $j 'approved' 'human'
        }

        'discard' {
            $j = Get-Job $Arg1
            Require-Stage $j 'backlog', 'ready' 'discard'
            Remove-JobBranch $j
            Move-Job $j 'discarded' 'human'
        }

        'purge' {
            $jobs = @($Board.jobs | Where-Object { $_.stage -in 'done', 'discarded' })
            foreach ($j in $jobs) { Remove-JobBranch $j }
            $left = @($Board.jobs | Where-Object { $_.stage -notin 'done', 'discarded' })
            $kept = @($left | ForEach-Object id)
            $ideas = @($Board.ideas | Where-Object { $_.status -in 'done', 'failed' -and -not (@($_.jobs) | Where-Object { $_ -in $kept }) })
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
            $ahead = (Invoke-Git (JobRoot $j) rev-list --count "main..factory/$($j.id)").Out
            if ($ahead -notmatch '^[1-9]\d*$') {
                Move-Job $j 'ready' 'human' 'merge-blocked' "nothing to merge: factory/$($j.id) adds no commits to main"
                Fail 'merge blocked: nothing to merge'
            }
            if (-not (Gate-Green $j)) {
                if ($Board.jobs | Where-Object { $_.stage -in $Building }) {
                    Move-Job $j 'ready' 'human' 'merge-blocked' 'gate must re-run but the worktree is busy; retry when Building is empty'
                    Fail 'merge blocked: worktree busy'
                }
                $g = Invoke-Gate $j.id
                $j = Get-Job $Arg1
                if ($g -ne $true) { Move-Job $j 'ready' 'gate' 'merge-blocked' $g; Fail "merge blocked: $g" }
                if (-not (Gate-Green $j)) { Move-Job $j 'ready' 'gate' 'gate-failed' 'gate red at merge; see gate.log'; Fail 'merge refused: gate red' }
            }
            $root = JobRoot $j
            $current = (Invoke-Git $root branch --show-current).Out
            if ($current -ne 'main') { Move-Job $j 'ready' 'human' 'merge-blocked' "release checkout is on '$current', not main"; Fail 'merge blocked: not on main' }
            # By content, not status: an EOL-only rewrite (autocrlf + a generator) is not a change.
            # safecrlf=false: Invoke-Git folds stderr in, and the CRLF warning is not a path.
            $dirty = (Invoke-Git $root -c core.quotepath=false -c core.safecrlf=false diff --name-only HEAD).Out
            if ($dirty) { Move-Job $j 'ready' 'human' 'merge-blocked' "release checkout is dirty: $(($dirty -split "`n") -join ', ')"; Fail 'merge blocked: dirty tree' }
            # Clean by content, so anything status still calls modified is stat or EOL only. git merge
            # would refuse to overwrite it; restoring it from HEAD changes no content. checkout alone
            # skips a file it judges unchanged, so the stale copy is removed first.
            foreach ($line in (Invoke-Git $root -c core.quotepath=false status --porcelain).Out -split "`n") {
                if ($line -notmatch '^\s*M\s+(.+)$') { continue }   # Invoke-Git trims the first line's leading space
                Remove-Item -LiteralPath (Join-Path $root $Matches[1]) -Force
                Invoke-Git $root checkout -- $Matches[1] | Out-Null
            }
            $r = Invoke-Git $root merge --no-ff "factory/$($j.id)" -m "Merge factory/$($j.id): $($j.title)"
            if ($r.Code -ne 0) {
                $conflicts = (Invoke-Git $root -c core.quotepath=false diff --name-only --diff-filter=U).Out
                Invoke-Git $root merge --abort | Out-Null
                $why = if ($conflicts) { "merge conflict: $(($conflicts -split "`n") -join ', ')" } else { ($r.Out -split "`n" | Where-Object { $_ -notmatch '^(Aborting|Please )' }) -join ' ' -replace '\s+', ' ' }
                Move-Job $j 'ready' 'human' 'merge-blocked' $why
                Fail "merge blocked: $($r.Out)"
            }
            $sha = (Invoke-Git $root rev-parse HEAD).Out
            $added = @(); $modified = @()
            foreach ($line in (Invoke-Git $root -c core.quotepath=false diff --name-status --no-renames "$sha^1" $sha).Out -split "`n") {
                if ($line -notmatch '^([A-Z])\t(.+)$') { continue }
                $full = Join-Path $root ($Matches[2] -replace '/', '\')
                if ($Matches[1] -eq 'A') { $added += $full } elseif ($Matches[1] -eq 'M') { $modified += $full }
            }
            $j.merged = [ordered]@{ sha = $sha; repo = $root; added = $added; modified = $modified }
            Move-Job $j 'done' 'human'
            Add-Event ([ordered]@{ job = $j.id; kind = 'merge'; detail = $sha })
            # A merged bootstrap releases its idea for a /factory:idea session.
            $i = if ($j.Contains('kind') -and $j.kind -eq 'bootstrap' -and $j.idea) { $Board.ideas | Where-Object { $_.id -eq $j.idea } | Select-Object -First 1 }
            if ($i -and $i.status -eq 'waiting') {
                $i.status = 'drafting'
                Save-Board $Board
                Add-Event ([ordered]@{ idea = $i.id; kind = 'idea'; detail = 'drafting' })
                "released $($i.id)"
            }
        }

        'next' {
            $n = Next-Runnable
            if ($n) { $n.id }
        }

        'checkout' {
            $j = Get-Job $Arg1
            $err = Ensure-Worktree $j.id
            if ($err) { Fail $err }
            WorktreePath (Get-Job $j.id)
        }

        'set-cost' {
            $j = Get-Job $Arg1
            $usd = [double]::Parse($Arg2, [Globalization.CultureInfo]::InvariantCulture)
            $ms = [long]$Arg3
            $j.costUsd = [Math]::Round([double]($j.costUsd ?? 0) + $usd, 4)
            $j.durationMs = [long]($j.durationMs ?? 0) + $ms
            Save-Board $Board
            Add-Event ([ordered]@{ job = $j.id; kind = 'cost'; costUsd = $usd; durationMs = $ms })
        }

        'set-stage' {
            $j = Get-Job $Arg1
            $to = $Arg2
            $by_ = if ($By) { $By } else { 'orchestrator' }
            switch ("$($j.stage)->$to") {
                'approved->build' {
                    $n = Next-Runnable
                    if (-not $n -or $n.id -ne $j.id) { Fail "approved->build refused: $($j.id) is not next in the queue, or a job is already building" }
                    Reset-Reviews $j
                    Move-Job $j 'build' $by_
                }
                'build->review' {
                    if (-not (Test-Path (Join-Path (JobDir $j.id) "round-$($j.round)/build.md"))) { Fail "build->review refused: round-$($j.round)/build.md missing" }
                    $j.reviews = [ordered]@{ security = 'running'; code = 'running'; ux = 'running'; ui = 'running' }
                    Move-Job $j 'review' $by_
                }
                default {
                    if ($to -eq 'ready' -and $j.stage -in $Building -and $Reason -eq 'escalated') {
                        Move-Job $j 'ready' $by_ 'escalated' $Detail
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
            if (-not $changes) { Move-Job $j 'approve' 'orchestrator' }
            elseif ([int]$j.round -lt 2) {
                $j.round = [int]$j.round + 1
                Reset-Reviews $j
                Move-Job $j 'build' 'orchestrator'
            }
            else { Move-Job $j 'ready' 'orchestrator' 'max-rounds' "CHANGES from $($changes -join ', ') at round $($j.round)" }
            $j.stage
        }

        'read-decision' {
            $j = Get-Job $Arg1
            Require-Stage $j 'approve' 'read-decision'
            $line = Read-FirstLine (Join-Path (JobDir $j.id) 'decision.md')
            if ($line -ceq 'Decision: APPROVE') { $j.decision = 'APPROVE'; Move-Job $j 'gate' 'orchestrator' }
            elseif ($line -ceq 'Decision: ESCALATE') { $j.decision = 'ESCALATE'; Move-Job $j 'ready' 'orchestrator' 'escalated' 'approver escalated; see decision.md' }
            else { Fail 'no decision line in decision.md' 2 }
            $j.stage
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
                foreach ($l in Get-Content $run.FullName | Where-Object { $_ -match '"type"\s*:\s*"result"' }) {
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
            [IO.File]::Move($tmp, (Join-Path $dir "$($j.id).json"), $true)
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
            [IO.File]::Move($tmp, $path, $true)
            Add-Event ([ordered]@{ job = $Arg1; kind = 'feedback'; detail = $rating })
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

        default { Fail "unknown command '$Command'" }
    }
}
finally {
    $Mutex.ReleaseMutex()
    $Mutex.Dispose()
}

exit 0
