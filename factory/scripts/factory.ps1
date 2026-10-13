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
    [switch] $Failed,
    [switch] $Json
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$Roles = 'security', 'code', 'ux', 'ui'
$Building = 'build', 'review', 'approve', 'gate'
$EventKinds = 'idea', 'stage', 'agent-start', 'agent-stop', 'gate', 'merge', 'cost', 'note'

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

function Next-Id([string] $prefix, $items) {
    $max = 0
    foreach ($x in $items) { if ($x.id -match "^$prefix-(\d+)$") { $max = [Math]::Max($max, [int]$Matches[1]) } }
    '{0}-{1:D3}' -f $prefix, ($max + 1)
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
            if (-not $Arg1) { Fail 'usage: new-idea <text> -Target <path>' }
            $err = Test-Target $Target
            if ($err) { Fail "new-idea refused: $err" }
            $id = Next-Id 'I' $Board.ideas
            $dir = Join-Path $Home_ "ideas/$id"
            New-Item -ItemType Directory -Force $dir | Out-Null
            Set-Content (Join-Path $dir 'idea.md') @($Arg1, '', "Target: $Target")
            $Board.ideas = @($Board.ideas) + [ordered]@{ id = $id; text = $Arg1; target = $Target; status = 'drafting'; jobs = @(); createdAt = Now }
            Save-Board $Board
            Add-Event ([ordered]@{ idea = $id; kind = 'idea'; detail = 'drafting' })
            $id
        }

        'add-spec' {
            $i = Get-Idea $Arg1
            if (-not $Arg2 -or -not (Test-Path $Arg2)) { Fail "spec file not found: $Arg2" }
            $lines = @(Get-Content $Arg2)
            $h = [Array]::FindIndex([string[]]$lines, [Predicate[string]] { param($l) $l -match '^#\s*Spec:' })
            if ($h -lt 0) { Fail "no '# Spec:' heading in $Arg2" }
            $title = ($lines[$h] -replace '^#\s*Spec:\s*', '').Trim()
            if ($title -match '\s[—-]\s(.+)$') { $title = $Matches[1].Trim() }
            if (-not $title) { Fail "empty title in $Arg2" }
            $id = Next-Id 'J' $Board.jobs
            $lines[$h] = "# Spec: $id — $title"
            New-Item -ItemType Directory -Force (JobDir $id) | Out-Null
            Set-Content (Join-Path (JobDir $id) 'spec.md') $lines
            $ts = Now
            $j = [ordered]@{
                id = $id; idea = $i.id; title = $title; stage = 'backlog'; round = 1; branch = "factory/$id"
                target = (Read-SpecMeta $Arg2).repo
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
            $branch = "factory/$($j.id)"
            $root = JobRoot $j
            if ((Test-Path (Join-Path $root '.git')) -and (Branch-Exists $root $branch)) {
                $wt = WorktreePath $j
                if ((Test-Path $wt) -and (Invoke-Git $wt branch --show-current).Out -eq $branch) {
                    $r = Invoke-Git $wt checkout -q --detach
                    if ($r.Code -ne 0) { Fail "cannot detach worktree: $($r.Out)" }
                }
                $r = Invoke-Git $root branch -D $branch
                if ($r.Code -ne 0) { Fail "cannot delete $($branch): $($r.Out)" }
            }
            Move-Job $j 'discarded' 'human'
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
            $dirty = (Invoke-Git $root status --porcelain).Out
            if ($dirty) { Move-Job $j 'ready' 'human' 'merge-blocked' 'release checkout is dirty'; Fail 'merge blocked: dirty tree' }
            $r = Invoke-Git $root merge --no-ff "factory/$($j.id)" -m "Merge factory/$($j.id): $($j.title)"
            if ($r.Code -ne 0) {
                Invoke-Git $root merge --abort | Out-Null
                Move-Job $j 'ready' 'human' 'merge-blocked' 'merge conflict'
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
