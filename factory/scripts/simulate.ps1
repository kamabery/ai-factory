<#
.SYNOPSIS
  Drives fake jobs through the factory using only factory.ps1 calls, so the dashboard can be
  watched and tested without spending a token.
.DESCRIPTION
  State goes to .factory-runs-sim/ and the git work happens in a throwaway sandbox repo at
  .factory-runs-sim/repo; the gate commands are injected (green). It never touches this repo's git,
  dotnet or npm. Watch it with:
    pwsh factory/scripts/serve.ps1 -StateDir .factory-runs-sim   (with the same env; see below)
    pwsh factory/scripts/simulate.ps1 -Reset -Speed 0.5
#>
param(
    [double] $Speed = 1.0,
    [switch] $Reset,
    [string] $StateDir
)

$ErrorActionPreference = 'Stop'
$Factory = Join-Path $PSScriptRoot 'factory.ps1'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $StateDir) { $StateDir = Join-Path $repoRoot '.factory-runs-sim' }
$StateDir = [IO.Path]::GetFullPath($StateDir)

$env:FACTORY_HOME = $StateDir
$env:FACTORY_REPO = Join-Path $StateDir 'repo'
Remove-Item Env:FACTORY_WORKTREE -ErrorAction SilentlyContinue   # each target uses its own <target>.wt
if (-not $env:FACTORY_GATE_CMDS) {
    $env:FACTORY_GATE_CMDS = '[{"name":"build","cmd":"exit 0"},{"name":"test","cmd":"exit 0"},{"name":"ui","cmd":"exit 0"}]'
}

function F {
    $out = & $Factory @args 2>&1
    $out | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }
}
function F-Must {
    $out = & $Factory @args
    if ($LASTEXITCODE -ne 0) { throw "factory.ps1 $($args -join ' ') failed ($LASTEXITCODE)" }
    $out
}
function Tick([double] $units = 1) { Start-Sleep -Milliseconds ([int](600 * $units / $Speed)) }
function Ev([string] $job, [string] $kind, [string] $agent) { F-Must event -Job $job -Kind $kind -Agent $agent | Out-Null }

if ($Reset -and (Test-Path $StateDir)) { Remove-Item -Recurse -Force $StateDir }
New-Item -ItemType Directory -Force $StateDir | Out-Null

if (-not (Test-Path (Join-Path $env:FACTORY_REPO '.git'))) {
    New-Item -ItemType Directory -Force $env:FACTORY_REPO | Out-Null
    git -C $env:FACTORY_REPO init -q -b main 2>&1 | Out-Null
    git -C $env:FACTORY_REPO config user.email 'sim@factory' | Out-Null
    git -C $env:FACTORY_REPO config user.name 'factory simulator' | Out-Null
    Set-Content (Join-Path $env:FACTORY_REPO 'README.md') 'simulator sandbox'
    git -C $env:FACTORY_REPO add -A 2>&1 | Out-Null
    git -C $env:FACTORY_REPO commit -q -m init 2>&1 | Out-Null
}

function Spec-Text([string] $title, [string] $intent) {
    @"
# Spec: $title

## Intent
$intent

## Target
- Repo: $($env:FACTORY_REPO)

## Gate
- build: ``exit 0``
- test: ``exit 0``

## Acceptance criteria
- [ ] AC1: $title works as described — proven by ``dotnet test tests/shelf.api.tests``
- [ ] AC2: the UI builds — proven by ``npm run build``

## Scope
- In: src/ui/react/src/routes
- Out: the API

## Risks
None beyond the usual.
"@
}

# Fake stream-json lines, shaped like `claude -p --output-format stream-json --verbose`.
function J([hashtable] $o) { $o | ConvertTo-Json -Compress -Depth 10 }
function Say([string] $text, [string] $parent = $null) { J @{ type = 'assistant'; parent_tool_use_id = $parent; message = @{ content = @(@{ type = 'text'; text = $text }) } } }
function Think([string] $parent = $null) { J @{ type = 'assistant'; parent_tool_use_id = $parent; message = @{ content = @(@{ type = 'thinking'; thinking = ''; signature = 'sim' }) } } }
function Tool([string] $id, [string] $name, [hashtable] $in) { J @{ type = 'assistant'; message = @{ content = @(@{ type = 'tool_use'; id = $id; name = $name; input = $in }) } } }
function Result([string] $id, [string] $text) { J @{ type = 'user'; message = @{ content = @(@{ type = 'tool_result'; tool_use_id = $id; content = $text }) } } }
function Done([double] $usd, [int] $ms) {
    $models = @{ 'claude-opus-5-5' = @{ costUSD = 0.38; outputTokens = 4200 }; 'claude-sonnet-5-5' = @{ costUSD = [Math]::Round($usd - 0.38, 2); outputTokens = 900 } }
    J @{ type = 'result'; subtype = 'success'; total_cost_usd = $usd; duration_ms = $ms; result = 'done'; modelUsage = $models }
}

function Sim-Idea([string] $text, [object[]] $specs) {
    $idea = F-Must new-idea $text -Target $env:FACTORY_REPO
    # The brief the /factory:idea session would have grilled out of the human.
    $unsure = if ($specs.Count) { 'Nothing.' } else { '- which list, and what counts as fast?' }
    Set-Content (Join-Path $StateDir "ideas/$idea/brief.md") "# Brief: $text`n`n## What it is for`n$text`n`n## Not sure`n$unsure"
    F-Must event -Idea $idea -Kind agent-start -Agent spec-writer | Out-Null
    Tick 2
    $ids = @()
    $k = 0
    foreach ($s in $specs) {
        $k++
        $file = Join-Path $StateDir "ideas/$idea/spec-$k.md"
        Set-Content $file (Spec-Text $s[0] $s[1])
        $ids += F-Must add-spec $idea $file
        Tick 0.5
    }
    F-Must event -Idea $idea -Kind agent-stop -Agent spec-writer | Out-Null
    if ($specs.Count) { F-Must idea-done $idea | Out-Null }
    else { F-Must idea-done $idea -Failed -Reason 'rejected in session: too vague' | Out-Null }
    , $ids
}

function Round([string] $id) { ((F-Must status -Json | ConvertFrom-Json).jobs | Where-Object id -eq $id).round }

function Sim-Build([string] $id) {
    $round = Round $id
    # The orchestrator's stream, and the builder's own transcript, as the Output tab reads them.
    $agentId = "sim$($id -replace '\D', '')r$round"
    $transcript = Join-Path $StateDir "transcripts/agent-$agentId.jsonl"
    New-Item -ItemType Directory -Force (Split-Path $transcript -Parent) | Out-Null
    Set-Content (Join-Path $StateDir "jobs/$id/run-$round.jsonl") @(
        (J @{ type = 'system'; subtype = 'init'; session_id = 'sim' }), (Say "Starting $id, round $round."),
        (Tool 't1' 'Agent' @{ subagent_type = 'factory:builder'; description = "Build $id" }),
        (Result 't1' 'Built and committed.'), (Done 0.42 95000))
    Set-Content $transcript @((Think), (Say 'Writing the failing test first.'),
        (Tool 'b1' 'Bash' @{ command = 'dotnet test' }), (Result 'b1' 'Failed: 1'), (Say 'Now the implementation.'))
    F-Must event (J @{ job = $id; kind = 'agent-start'; agent = 'builder'; agentId = $agentId; transcript = $transcript }) | Out-Null
    $wt = F-Must checkout $id
    Add-Content (Join-Path $wt "$id.txt") "round $round"
    git -C $wt add -A 2>&1 | Out-Null
    git -C $wt -c user.email=sim@factory -c user.name=simulator commit -q -m "$($id): round $round" 2>&1 | Out-Null
    $dir = Join-Path $StateDir "jobs/$id/round-$round"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content (Join-Path $dir 'build.md') "# Build: $id — round $round`n`nBranch: factory/$id`n`n## Evidence`n- simulated → exit 0"
    Tick 3
    Ev $id agent-stop builder
    F-Must set-stage $id review | Out-Null
}

function Sim-Review([string] $id, [hashtable] $verdicts) {
    $round = Round $id
    $dir = Join-Path $StateDir "jobs/$id/round-$round"
    foreach ($r in 'security', 'code', 'ux', 'ui') { Ev $id agent-start "$r-reviewer" }
    foreach ($r in 'security', 'code', 'ux', 'ui') {
        Tick 1
        $v = if ($verdicts.ContainsKey($r)) { $verdicts[$r] } else { 'PASS' }
        $body = if ($v -eq 'CHANGES') { "## Findings`n- src/ui/react/src/routes/books.tsx:42 uses a raw colour; use the token." } else { "## Findings`nNone." }
        Set-Content (Join-Path $dir "review-$r.md") "Verdict: $v`n`n# $r review: $id — round $round`n`n$body"
        F-Must set-review $id $r $v | Out-Null
        Ev $id agent-stop "$r-reviewer"
    }
    F read-reviews $id | Out-Null
}

function Sim-Approve([string] $id, [string] $decision = 'APPROVE') {
    Ev $id agent-start approver
    Tick 2
    Set-Content (Join-Path $StateDir "jobs/$id/decision.md") "Decision: $decision`n`n# Decision: $id`n`n## Basis`nEvery AC evidenced; every review PASS."
    Ev $id agent-stop approver
    F-Must read-decision $id | Out-Null
}

function Sim-Start([string] $id) {
    Tick 1
    F-Must set-stage $id build -By runner | Out-Null
    F-Must set-cost $id 0.42 95000 | Out-Null
}

Write-Host "simulating into $StateDir (speed $Speed)"

$a = Sim-Idea 'Let me find books quickly' @(
    , @('Book search filter', 'On /books, a text box filters the list by name as you type.')
    , @('Sort books by name', 'The /books list is sorted by name, ascending.')
    , @('Page the book list', 'The /books list shows 20 books per page.')
)
$b = Sim-Idea 'Log every request' @(, @('Request logging', 'Log every incoming request.'))
Sim-Idea 'Make it faster' @() | Out-Null
$c = Sim-Idea 'Unknown routes should not look broken' @(, @('Not-found page', 'An unknown route renders a not-found page.'))

# J-001: the happy path, all the way to Done.
F-Must approve $a[0] | Out-Null
Sim-Start $a[0]; Sim-Build $a[0]; Sim-Review $a[0] @{}; Sim-Approve $a[0]
F gate $a[0] | Out-Null
Tick 1
F-Must merge $a[0] | Out-Null

# J-002: CHANGES twice → max-rounds; a human rework; CHANGES again → max-rounds.
F-Must approve $a[1] | Out-Null
Sim-Start $a[1]; Sim-Build $a[1]; Sim-Review $a[1] @{ ui = 'CHANGES' }
Sim-Build $a[1]; Sim-Review $a[1] @{ ui = 'CHANGES' }
Tick 1
F-Must rework $a[1] 'use the colour tokens from docs/DESIGN.md' | Out-Null
Sim-Start $a[1]; Sim-Build $a[1]; Sim-Review $a[1] @{ ui = 'CHANGES' }

# J-003: green, but the release checkout is dirty at merge → merge-blocked. Then "the human" cleans up.
F-Must approve $a[2] | Out-Null
Sim-Start $a[2]; Sim-Build $a[2]; Sim-Review $a[2] @{}; Sim-Approve $a[2]
F gate $a[2] | Out-Null
# Dirty by content: an edit to a tracked file. An untracked file is left to git and does not block.
Add-Content (Join-Path $env:FACTORY_REPO 'README.md') 'uncommitted'
F merge $a[2] | Out-Null
git -C $env:FACTORY_REPO checkout -- README.md 2>&1 | Out-Null

# J-004: discarded from the backlog. J-005 stays in the backlog for a human.
Tick 1
F-Must discard $b[0] | Out-Null

# I-005 / J-006: a new target. The bootstrap job reaches Ready for review; its idea waits for the merge.
$boot = (F-Must new-idea 'A todo app, bootstrapped' -Target (Join-Path $StateDir 'apps/todo') `
    -Scaffold '{"name":"todo","resource":"Todo","db":"sqlite","ui":"react","events":false}') -split ' '
F-Must approve $boot[1] | Out-Null
Sim-Start $boot[1]; Sim-Build $boot[1]; Sim-Review $boot[1] @{}; Sim-Approve $boot[1]
F gate $boot[1] | Out-Null

# Evaluations of the merged J-001 and the max-rounds J-002, as a human session would write them.
foreach ($e in @(@($a[0], 'good', 'Clean run: one round, every AC evidenced.'), @($a[1], 'mixed', 'The ui reviewer and the builder never converged.'))) {
    $file = Join-Path $StateDir "eval-$($e[0]).md"
    Set-Content $file "Rating: $($e[1])`n`n# Eval: $($e[0])`n`n## Verdict`n$($e[2])"
    F-Must eval $e[0] -File $file | Out-Null
    Remove-Item $file
}

Write-Host 'simulation complete'
F-Must status
