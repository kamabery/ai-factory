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
$Building = 'build', 'review', 'deploy', 'approve', 'gate'
$EventKinds = 'idea', 'stage', 'agent-start', 'agent-stop', 'gate', 'merge', 'cost', 'note', 'eval', 'feedback', 'deploy', 'destroy', 'lesson'
$Providers = 'subscription', 'openrouter'

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

. (Join-Path $PSScriptRoot 'github.ps1')
. (Join-Path $PSScriptRoot 'hcp.ps1')
. (Join-Path $PSScriptRoot 'deploy.ps1')

# A job's local clone of its target, where it builds and is gated; deleted at done or discarded.
function WorkRoot {
    if ($env:FACTORY_WORK) { return $env:FACTORY_WORK }
    Join-Path ([IO.Path]::GetTempPath()) 'claude-factory'
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

# The bootstrap job's spec: factory/templates/bootstrap.md with the human's scaffold options filled in.
function Bootstrap-Spec($sc, [string] $target) {
    $n = $sc.name; $r = $sc.resource
    $cmds = @("dotnet new aspire-base -n $n -o . --dbProvider $($sc.db) --resource $r")
    # aspire-addon reads name, resource and data store (so the event store) from .aspirestarter.json.
    switch ($sc.ui) {
        'react' { $cmds += 'aspire-addon add ui-react' }
        'blazor' { $cmds += 'aspire-addon add ui-blazor --web true --maui false' }
        'blazor-maui' { $cmds += 'aspire-addon add ui-blazor --web true --maui true' }
    }
    if ($sc.events) { $cmds += 'aspire-addon add events' }
    # After the front end: auth reads which one from the manifest.
    if ($sc.Contains('auth') -and $sc.auth) { $cmds += 'aspire-addon add auth' }
    $gate = @("- build: ``dotnet build $n.slnx``", "- test: ``dotnet test $n.slnx``", '- apphost: `dotnet build .aspire/apphost.cs`',
              '- infra: `aspire-addon infra check`')
    if ($sc.ui -eq 'react') { $gate += '- ui: `Set-Location src/ui/react; if (-not (Test-Path node_modules/.package-lock.json) -or (Get-Item package-lock.json).LastWriteTime -gt (Get-Item node_modules/.package-lock.json).LastWriteTime) { npm ci }; npm run build`' }
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

# Replaces $path with $tmp. A reader that opened $path without delete sharing (serve.ps1 serving
# /state/board.json to the dashboard poll) makes the replace fail for a moment, so it is retried.
function Replace-File([string] $tmp, [string] $path) {
    for ($i = 0; ; $i++) {
        try { [IO.File]::Move($tmp, $path, $true); return }
        catch [UnauthorizedAccessException], [IO.IOException] { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 50 }
    }
}

function Save-Board($b) {
    $tmp = "$BoardPath.$PID.tmp"
    [IO.File]::WriteAllText($tmp, ($b | ConvertTo-Json -Depth 20))
    Replace-File $tmp $BoardPath
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
    $provider = (Read-EnvFile (EnvFilePath))['FACTORY_PROVIDER']
    if (-not $provider) { $provider = 'subscription' }
    if ($provider -notin $Providers) { Fail "FACTORY_PROVIDER '$provider' in .env must be $($Providers -join ' or ')" }
    $id = Next-Id 'J' $Board.jobs
    $lines[$h] = "# Spec: $id — $title"
    New-Item -ItemType Directory -Force (JobDir $id) | Out-Null
    $spec = Join-Path (JobDir $id) 'spec.md'
    Set-Content $spec $lines
    $ts = Now
    $j = [ordered]@{
        id = $id; idea = $i.id; kind = $kind; title = $title; stage = 'backlog'; round = 1; branch = "factory/$id"
        target = (Read-SpecMeta $spec).repo; provider = $provider
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
    $j.history = @($j.history) + [ordered]@{ ts = $ts; from = $from; to = $to; by = $by }
    Save-Board $Board
    $e = [ordered]@{ job = $j.id; kind = 'stage'; from = $from; to = $to; by = $by }
    if ($reason) { $e.reason = $reason }
    Add-Event $e
}

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
# after a rework, otherwise a new one from origin/main.
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
    $r = if ((Invoke-Git $wt rev-parse --verify --quiet "refs/remotes/origin/$branch").Code -eq 0) { Invoke-Git $wt checkout -q -B $branch "origin/$branch" }
         elseif ((Invoke-Git $wt rev-parse --verify --quiet 'refs/remotes/origin/main').Code -eq 0) { Invoke-Git $wt checkout -q -b $branch origin/main }
         else { return "$($j.target) has no main branch" }
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
        $res = Invoke-GitHub POST "/repos/$($j.target)/pulls" @{ title = "$($j.id): $($j.title)"; head = $branch; base = 'main'
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
            if (-not $Arg1) { Fail 'usage: new-idea <text> -Target <owner/name> [-Scaffold <json>]' }
            $err = Test-Target $Target
            if ($err) { Fail "new-idea refused: $err" }
            $new = Test-NewTarget $Target 'new-idea'
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

        'set-provider' {
            $j = Get-Job $Arg1
            Require-Stage $j 'backlog', 'approved' 'set-provider'
            if ($Arg2 -notin $Providers) { Fail "set-provider refused: provider must be $($Providers -join ' or ')" }
            $j.provider = $Arg2
            $j.updatedAt = Now
            Save-Board $Board
            Add-Event ([ordered]@{ job = $j.id; kind = 'note'; detail = "provider $Arg2" })
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
            Require-Stage $j 'ready' 'rework'
            if (-not $Arg2) { Fail 'usage: rework <id> <note>' }
            $j.round = [int]$j.round + 1
            Set-Content (Join-Path (JobDir $j.id) "rework-$($j.round).md") $Arg2
            Reset-Reviews $j
            $j.decision = $null
            $j.approvedAt = Now
            if ($j.Contains('deploy') -and $j.deploy) { $j.deploy.attempts = 0 }
            Move-Job $j 'approved' 'human'
            Start-Destroy $j
        }

        'discard' {
            $j = Get-Job $Arg1
            Require-Stage $j 'backlog', 'ready' 'discard'
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
            # The human may have merged the PR on GitHub already: that merge is the acceptance.
            $onGitHub = Get-GitHubMerge $j
            if ($onGitHub) { Complete-GitHubMerge $j $onGitHub; break }
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
            $n = Next-Runnable
            if ($n) { $n.id }
        }

        'checkout' {
            $j = Get-Job $Arg1
            $err = Ensure-Clone $j.id
            if ($err) { Fail $err }
            WorkDir $j
        }

        'publish' {
            $j = Get-Job $Arg1
            Require-Stage $j 'ready' 'publish'
            $url = Publish-Job $j
            if ($url -like 'error: *') { Fail "publish refused: $($url.Substring(7))" }
            $url
        }

        'clone-target' {
            $err = Test-Target $Arg1
            if ($err) { Fail "clone-target refused: $err" }
            if (-not $Arg2 -or -not [IO.Path]::IsPathFullyQualified($Arg2)) { Fail 'usage: clone-target <owner/name> <absolute dir>' }
            Remove-Item -LiteralPath $Arg2 -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force (Split-Path $Arg2 -Parent) | Out-Null
            $r = Invoke-GitAuth (Split-Path $Arg2 -Parent) clone -q --depth 1 --branch main (CloneUrl $Arg1) $Arg2
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
            Add-Event ([ordered]@{ job = $j.id; kind = 'cost'; costUsd = $usd; durationMs = $ms; provider = $j['provider'] ?? 'subscription' })
        }

        'deploy' {
            $j = Get-Job $Arg1
            Require-Stage $j 'deploy' 'deploy'
            if (-not (Deploys-On)) { Fail 'deploy refused: deploys are off (no HCP_ORG in .env)' }
            $d = Get-Deploy $j
            if ([int]$d.attempts -ge 3) { Fail "deploy refused: $($j.id) has used its 3 attempts" }
            $d.attempts = [int]$d.attempts + 1
            $d.status = 'applying'
            Save-Board $Board
            $attempt = $d.attempts
            $res = Invoke-Deploy $j
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
                if ($attempt -ge 3) { Move-Job $j 'ready' 'orchestrator' 'deploy-failed' $first }
                Fail "deploy attempt $attempt failed: $first; see deploy-$attempt.log (factory.ps1 deploy-log $($j.id))"
            }
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
            $before = $j.deploy.status
            $j.deploy.status = 'destroying'
            Save-Board $Board
            $err = Invoke-Destroy $j
            $j = Get-Job $Arg1
            if ($err) {
                $j.deploy.status = $before
                Save-Board $Board
                Add-Event ([ordered]@{ job = $j.id; kind = 'destroy'; detail = "failed: $err" })
                Fail "destroy failed: $err"
            }
            $j.deploy.status = 'down'
            $j.deploy.urls = $null
            Save-Board $Board
            Add-Event ([ordered]@{ job = $j.id; kind = 'destroy'; detail = 'down' })
            'down'
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
            if (-not $changes) {
                if (Deploys-On) { Move-Job $j 'deploy' 'orchestrator' }
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
            else { Move-Job $j 'ready' 'orchestrator' 'max-rounds' "CHANGES from $($changes -join ', ') at round $($j.round)" }
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
                if ($g -and $g -notmatch '^(?i)none\.?$') { $gaps += [pscustomobject]@{ round = $d.Name; text = $g } }
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

        default { Fail "unknown command '$Command'" }
    }
}
finally {
    $Mutex.ReleaseMutex()
    $Mutex.Dispose()
}

exit 0
