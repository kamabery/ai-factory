<#
.SYNOPSIS
  The factory dashboard server: http://localhost:4317. See "Dashboard API" in docs/FACTORY-CONTRACT.md.
.DESCRIPTION
  Serves factory/dashboard/index.html, its vendor/ files, read-only files under the state dir
  (/state/...) and agent transcripts named by agent-start events. Every board write is one
  factory.ps1 call; this script holds no transition logic. After an approve or rework it starts
  runner.ps1 detached. Idea sessions (/api/sessions) store their conversation under sessions/<S>/,
  and each assistant turn is a detached idea-turn.ps1. This script only checks that the .env has
  a key; it never reads the key into the environment.
#>
param(
    [int] $Port = 4317,
    [string] $StateDir,
    [string] $EnvFile,
    [int] $WatchdogSeconds = 10,
    [int] $PrSyncSeconds = 30
)

$ErrorActionPreference = 'Stop'
$Scripts = $PSScriptRoot
$Factory = Join-Path $Scripts 'factory.ps1'
$Index = Join-Path (Split-Path $Scripts -Parent) 'dashboard/index.html'
$Vendor = Join-Path (Split-Path $Scripts -Parent) 'dashboard/vendor'
if (-not $EnvFile) { $EnvFile = Join-Path (Split-Path (Split-Path $Scripts -Parent) -Parent) '.env' }
$env:FACTORY_ENV_FILE = $EnvFile   # factory.ps1 reads the GitHub settings from the same file

if (-not $StateDir) {
    $StateDir = if ($env:FACTORY_HOME) { $env:FACTORY_HOME } else {
        Join-Path (Split-Path ((git rev-parse --path-format=absolute --git-common-dir) -replace '/', '\') -Parent) '.factory-runs'
    }
}
$StateDir = [IO.Path]::GetFullPath($StateDir)
New-Item -ItemType Directory -Force $StateDir | Out-Null
$env:FACTORY_HOME = $StateDir
$Sessions = Join-Path $StateDir 'sessions'

$Types = @{
    '.html' = 'text/html'; '.json' = 'application/json'; '.jsonl' = 'text/plain'; '.md' = 'text/plain'
    '.log' = 'text/plain'; '.txt' = 'text/plain'; '.js' = 'text/javascript'; '.css' = 'text/css'
}

function Invoke-Factory {
    $out = & pwsh -NoProfile -File $Factory @args 2>&1
    [pscustomobject]@{
        Ok = ($LASTEXITCODE -eq 0)
        Message = (($out | ForEach-Object { "$_" }) -join "`n").Trim() -replace '^factory: ', ''
    }
}

function Start-Detached([string] $script, [string[]] $argv = @()) {
    $a = @('-NoProfile', '-File', (Join-Path $Scripts $script)) + $argv
    if ($IsWindows) { Start-Process pwsh -ArgumentList $a -WindowStyle Hidden | Out-Null }
    else { Start-Process pwsh -ArgumentList $a | Out-Null }
}

# Starts the runner when Approved is not empty and no live runner holds the lock: one that died, or
# an approval made outside the dashboard. A lock younger than 5 s may not hold its PID yet.
$NextWatch = [DateTime]::MinValue
function Invoke-Watchdog {
    if ([DateTime]::UtcNow -lt $script:NextWatch) { return }
    $script:NextWatch = [DateTime]::UtcNow.AddSeconds($WatchdogSeconds)
    try {
        if (-not ((Read-Board).jobs | Where-Object stage -eq 'approved')) { return }
        $lock = Join-Path $StateDir 'runner.lock'
        if (Test-Path $lock) {
            if ((Get-Item $lock).LastWriteTimeUtc -gt [DateTime]::UtcNow.AddSeconds(-5)) { return }
            $holder = (Get-Content $lock -ErrorAction SilentlyContinue | Select-Object -First 1) -as [int]
            if ($holder -and (Get-Process -Id $holder -ErrorAction SilentlyContinue)) { return }
        }
        Start-Detached 'runner.ps1'
        Add-Content -LiteralPath (Join-Path $StateDir 'runner.log') "$([DateTime]::UtcNow.ToString('o')) watchdog: started runner" -Encoding utf8
    } catch { Write-Warning "watchdog: $_" }
}

# A ready job's PR may be merged on GitHub by the human: factory.ps1 sync-prs then moves it to done.
# Detached, so the GitHub calls never hold up a request.
$NextPrSync = [DateTime]::MinValue
function Invoke-PrSync {
    if ([DateTime]::UtcNow -lt $script:NextPrSync) { return }
    $script:NextPrSync = [DateTime]::UtcNow.AddSeconds($PrSyncSeconds)
    try {
        if ((Read-Board).jobs | Where-Object { $_.stage -eq 'ready' -and $_.pr }) { Start-Detached 'factory.ps1' @('sync-prs') }
    } catch { Write-Warning "pr sync: $_" }
}

function Read-Board {
    $p = Join-Path $StateDir 'board.json'
    if (Test-Path $p) { Get-Content $p -Raw | ConvertFrom-Json } else { [pscustomobject]@{ jobs = @(); ideas = @() } }
}

function Send($ctx, [int] $status, [string] $body, [string] $type = 'application/json') {
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $ctx.Response.StatusCode = $status
    $ctx.Response.ContentType = "$type; charset=utf-8"
    $ctx.Response.Headers['Cache-Control'] = 'no-store'
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}

function Send-Result($ctx, $r) {
    Send $ctx ($(if ($r.Ok) { 200 } else { 409 })) (@{ ok = $r.Ok; message = $r.Message } | ConvertTo-Json -Compress)
}

function Send-File($ctx, [string] $path) {
    if (-not (Test-Path $path -PathType Leaf)) { return Send $ctx 404 '{"ok":false,"message":"not found"}' }
    $ext = [IO.Path]::GetExtension($path).ToLowerInvariant()
    $type = if ($Types.ContainsKey($ext)) { $Types[$ext] } else { 'application/octet-stream' }
    # Not File.ReadAllBytes: its FileShare.Read would block the runner appending to a live run log.
    $fs = [IO.FileStream]::new($path, 'Open', 'Read', [IO.FileShare]'ReadWrite, Delete')
    try { $bytes = [byte[]]::new($fs.Length); $n = 0; while ($n -lt $bytes.Length) { $k = $fs.Read($bytes, $n, $bytes.Length - $n); if ($k -le 0) { break }; $n += $k } }
    finally { $fs.Dispose() }
    $ctx.Response.StatusCode = 200
    $ctx.Response.ContentType = "$type; charset=utf-8"
    $ctx.Response.Headers['Cache-Control'] = 'no-store'
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}

function Read-Body($ctx) {
    $reader = [IO.StreamReader]::new($ctx.Request.InputStream, [Text.Encoding]::UTF8)
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}

# --- idea sessions (see "Idea sessions" in the contract) ------------------------------------------

# The value of one .env line, or $null. Only the key's presence and the model are ever read here.
function Get-EnvValue([string] $name) {
    if (-not (Test-Path $EnvFile)) { return $null }
    $line = Get-Content $EnvFile | Where-Object { $_ -match "^\s*$name\s*=\s*\S" } | Select-Object -Last 1
    if ($line) { ($line -replace "^\s*$name\s*=\s*", '').Trim().Trim('"', "'") }
}
function Get-IdeaModel { (Get-EnvValue 'FACTORY_IDEA_MODEL') ?? 'claude-opus-5-5' }

function Read-Json([string] $path) { Get-Content $path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -DateKind String }
# Atomic, with the rename retried while a turn process or a reader holds the target (as idea-turn.ps1).
function Write-Json([string] $path, $o) {
    $tmp = "$path.$PID.tmp"
    [IO.File]::WriteAllText($tmp, ($o | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    for ($i = 0; ; $i++) {
        try { [IO.File]::Move($tmp, $path, $true); return }
        catch [UnauthorizedAccessException], [IO.IOException] { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 50 }
    }
}
function SessionDir([string] $id) { Join-Path $Sessions $id }
# The session's shallow clone of its target, for the model's read tools; see "Work directories".
function SessionClone([string] $id) {
    $work = if ($env:FACTORY_WORK) { $env:FACTORY_WORK } else { Join-Path ([IO.Path]::GetTempPath()) 'claude-factory' }
    Join-Path $work "sessions/$id"
}
function Read-Session([string] $id) { Read-Json (Join-Path (SessionDir $id) 'session.json') }
function Save-Session($s) { Write-Json (Join-Path (SessionDir $s.id) 'session.json') $s }
function Read-Chat([string] $id) {
    $l = [Collections.Generic.List[object]]::new()
    foreach ($m in @((Read-Json (Join-Path (SessionDir $id) 'chat.json')).messages)) { $l.Add($m) }
    , $l
}
function Save-Chat([string] $id, $messages) { Write-Json (Join-Path (SessionDir $id) 'chat.json') ([ordered]@{ messages = $messages }) }

# A turn is running while a live process holds turn.lock, or just after serve started one (the
# process may not hold the lock yet).
function Test-Busy($s) {
    $lock = Join-Path (SessionDir $s.id) 'turn.lock'
    if (Test-Path $lock) {
        $holder = (Get-Content $lock -ErrorAction SilentlyContinue | Select-Object -First 1) -as [int]
        if ($holder -and (Get-Process -Id $holder -ErrorAction SilentlyContinue)) { return $true }
    }
    $s.status -eq 'running' -and (Get-Item (Join-Path (SessionDir $s.id) 'session.json')).LastWriteTimeUtc -gt [DateTime]::UtcNow.AddSeconds(-15)
}

function Start-Turn($s) {
    $s.status = 'running'; $s.error = $null
    Save-Session $s
    Start-Detached 'idea-turn.ps1' @('-Session', $s.id, '-StateDir', $StateDir, '-EnvFile', $EnvFile)
}

# Adds user content: a new message, or more blocks in the last one when it is already the user's.
function Add-User($messages, [object[]] $blocks) {
    $last = if ($messages.Count) { $messages[$messages.Count - 1] } else { $null }
    if ($last -and $last.role -eq 'user') { $last.content = @($last.content) + $blocks }
    else { $messages.Add([ordered]@{ role = 'user'; content = $blocks }) }
}

# Answers the pending UI tool (with any server results from the same message) and clears it.
function Add-Answer($s, $messages, [string] $text) {
    $blocks = @($s.pendingResults) + [ordered]@{ type = 'tool_result'; tool_use_id = $s.pending.id; content = $(if ($text) { $text } else { '(no answer)' }) }
    Add-User $messages $blocks
    $s.pending = $null; $s.pendingResults = @()
}

# The human's words, verbatim and in order, so an idea can be tried again; see "Idea sessions".
function Add-Human([string] $id, [string] $kind, [string] $text, $extra = @{}) {
    $line = [ordered]@{ ts = [DateTime]::UtcNow.ToString('o'); kind = $kind; text = $text }
    foreach ($k in $extra.Keys) { $line[$k] = $extra[$k] }
    [IO.File]::AppendAllText((Join-Path (SessionDir $id) 'human.jsonl'), ($line | ConvertTo-Json -Depth 100 -Compress) + "`n", [Text.UTF8Encoding]::new($false))
}

function Refuse([string] $msg) { [pscustomobject]@{ Ok = $false; Message = $msg } }

function Handle-Session($ctx, [string] $method, [string] $id, [string] $action, [string] $k) {
    if (-not (Test-Path (Join-Path (SessionDir $id) 'session.json'))) { return Send $ctx 404 '{"ok":false,"message":"no such session"}' }
    $s = Read-Session $id
    if ($s.status -eq 'closed') { return Send-Result $ctx (Refuse "$id is closed") }
    if (Test-Busy $s) { return Send-Result $ctx (Refuse "$id is busy: wait for the current turn") }
    $req = if ($method -eq 'POST') { $b = Read-Body $ctx; if ($b) { $b | ConvertFrom-Json -AsHashtable } else { @{} } } else { $null }
    $messages = Read-Chat $id

    switch ($action) {
        'reply' {
            $text = "$($req.text ?? '')".Trim()
            if ($req.scaffold -and -not $s.target) {
                # Add new: the confirmed scaffold names the repo, which must not exist yet.
                $t = "$(Get-EnvValue 'GITHUB_OWNER')/$($req.scaffold.repo)"
                $r = Invoke-Factory check-target $t
                if (-not $r.Ok) { return Send-Result $ctx $r }
                if ($r.Message -ne 'new') { return Send-Result $ctx (Refuse "$t already exists: pick it from the list instead") }
                $s.target = $t
                $text = "$text The target is $t.".Trim()
            }
            if ($req.scaffold) { $req.scaffold.Remove('repo'); $s.scaffold = $req.scaffold }
            if ($s.pending) {
                Add-Human $id 'answer' $text @{ asked = [ordered]@{ name = $s.pending.name; input = $s.pending.input } }
                Add-Answer $s $messages $text
            }
            elseif ($text) {
                Add-Human $id 'message' $text
                Add-User $messages @([ordered]@{ type = 'text'; text = $text })
            }
            elseif (-not $messages.Count -or $messages[$messages.Count - 1].role -ne 'user') { return Send-Result $ctx (Refuse 'nothing to answer') }
            Save-Chat $id $messages
            Start-Turn $s
            return Send-Result $ctx ([pscustomobject]@{ Ok = $true; Message = 'turn started' })
        }
        'brief' {
            if ($s.pending.name -ne 'submit_brief') { return Send-Result $ctx (Refuse 'there is no brief to confirm') }
            if ($s.newTarget -and -not $s.scaffold) { return Send-Result $ctx (Refuse 'confirm the scaffold options first') }
            $md = "$($s.pending.input.markdown)"
            $title = if ($md -match '(?m)^#\s*Brief:\s*(.+?)\s*$') { $Matches[1] } else { $s.text }
            $r = if ($s.newTarget) { Invoke-Factory new-idea $title -Target $s.target -Scaffold ($s.scaffold | ConvertTo-Json -Compress) }
                 else { Invoke-Factory new-idea $title -Target $s.target }
            if (-not $r.Ok) { return Send-Result $ctx $r }
            $ids = $r.Message -split '\s+'
            $s.idea = $ids[0]
            if ($ids.Count -gt 1) { $s.bootstrap = $ids[1] }
            Set-Content (Join-Path $StateDir "ideas/$($s.idea)/brief.md") $md -Encoding utf8
            if ($s.bootstrap) {
                Add-Answer $s $messages "The human confirmed the brief; it is saved as idea $($s.idea). The target is being scaffolded by bootstrap job $($s.bootstrap). You will be told when it has merged; until then there is nothing to do."
                Save-Chat $id $messages
                Save-Session $s
            } else {
                Add-Answer $s $messages "The human confirmed the brief; it is saved as idea $($s.idea). Now be the spec-writer: read the target, then call submit_specs."
                Save-Chat $id $messages
                Start-Turn $s
            }
            return Send-Result $ctx $r
        }
        'write-specs' {
            $idea = if ($s.idea) { (Read-Board).ideas | Where-Object id -eq $s.idea }
            if (-not $idea) { return Send-Result $ctx (Refuse 'the brief is not confirmed yet') }
            if ($idea.status -ne 'drafting') { return Send-Result $ctx (Refuse "$($s.idea) is $($idea.status): merge the bootstrap job $($s.bootstrap) first") }
            $r = Invoke-Factory clone-target $s.target (SessionClone $id)
            if (-not $r.Ok) { return Send-Result $ctx $r }
            $s.checkout = SessionClone $id
            Save-Session $s
            Add-User $messages @([ordered]@{ type = 'text'; text = "The bootstrap job $($s.bootstrap) has merged, so $($s.target) now exists. Read it, then call submit_specs." })
            Save-Chat $id $messages
            Start-Turn $s
            return Send-Result $ctx ([pscustomobject]@{ Ok = $true; Message = 'turn started' })
        }
        'specs' {
            $entry = @($s.specs) | Where-Object { $_.k -eq [int]$k } | Select-Object -First 1
            if (-not $entry) { return Send-Result $ctx (Refuse "there is no spec $k") }
            if ($entry.decision) { return Send-Result $ctx (Refuse "spec $k is already decided ($($entry.decision))") }
            $file = Join-Path $StateDir "ideas/$($s.idea)/spec-$k.md"
            if ($method -eq 'PUT') {
                $md = Read-Body $ctx
                if ($md -notmatch '(?m)^#\s*Spec:\s*(.+?)\s*$') { return Send-Result $ctx (Refuse "the spec needs a '# Spec: <title>' line") }
                $entry.title = $Matches[1]
                Set-Content $file $md -NoNewline -Encoding utf8
                Save-Session $s
                return Send-Result $ctx ([pscustomobject]@{ Ok = $true; Message = 'spec saved' })
            }
            $r = switch ($req.decision) {
                'rejected' { [pscustomobject]@{ Ok = $true; Message = '' } }
                { $_ -in 'approved', 'backlog' } { Invoke-Factory add-spec $s.idea $file }
                default { Refuse "decision '$($req.decision)' must be approved, backlog or rejected" }
            }
            if (-not $r.Ok) { return Send-Result $ctx $r }
            $entry.decision = $req.decision
            if ($req.decision -ne 'rejected') { $entry.job = $r.Message }
            if ($req.decision -eq 'approved') {
                $a = Invoke-Factory approve $entry.job
                if ($a.Ok) { Start-Detached 'runner.ps1' }
                else { $entry.decision = 'backlog'; $r = Refuse "$($entry.job) is in Backlog: $($a.Message)" }
            }
            Save-Session $s
            return Send-Result $ctx $r
        }
        'close' {
            $r = [pscustomobject]@{ Ok = $true; Message = 'closed' }
            if ($s.idea) {
                $r = if ($req.failed) { Invoke-Factory idea-done $s.idea -Failed -Reason ($req.reason ?? 'every spec rejected in session') } else { Invoke-Factory idea-done $s.idea }
                if (-not $r.Ok) { return Send-Result $ctx $r }
            }
            $s.status = 'closed'
            Save-Session $s
            Remove-Item -LiteralPath (SessionClone $id) -Recurse -Force -ErrorAction SilentlyContinue
            return Send-Result $ctx $r
        }
    }
    Send $ctx 404 '{"ok":false,"message":"not found"}'
}

function New-Session($ctx) {
    $req = Read-Body $ctx | ConvertFrom-Json -AsHashtable
    if (-not (Get-EnvValue 'ANTHROPIC_API_KEY')) { return Send-Result $ctx (Refuse "no ANTHROPIC_API_KEY in $EnvFile") }
    if (-not "$($req.text)".Trim()) { return Send-Result $ctx (Refuse 'describe the idea first') }
    $owner = Get-EnvValue 'GITHUB_OWNER'
    if ($req.new) {
        # Add new: no repo yet; the model names it in propose_scaffold.
        if (-not $owner) { return Send-Result $ctx (Refuse "Add new needs GITHUB_OWNER in $EnvFile") }
        $req.target = $null
        $new = $true
    } else {
        $r = Invoke-Factory check-target "$($req.target)"
        if (-not $r.Ok) { return Send-Result $ctx $r }
        $new = $r.Message -eq 'new'
    }
    $n = 0
    if (Test-Path $Sessions) { foreach ($d in Get-ChildItem $Sessions -Directory) { if ($d.Name -match '^S-(\d+)$') { $n = [Math]::Max($n, [int]$Matches[1]) } } }
    $id = 'S-{0:D3}' -f ($n + 1)
    $checkout = $null
    if (-not $new) {
        $checkout = SessionClone $id
        $r = Invoke-Factory clone-target "$($req.target)" $checkout
        if (-not $r.Ok) { return Send-Result $ctx $r }
    }
    New-Item -ItemType Directory -Force (SessionDir $id) | Out-Null
    $about = if (-not $req.target) { "This is a new target with no repo yet. Grill enough to know what it is, then call propose_scaffold, including repo: a short lower-case GitHub repo name. The owner is $owner. A bootstrap job will create the repo and scaffold it from template-projects." }
             elseif ($new) { 'This is a new target: the GitHub repo does not exist yet (or is empty), and a bootstrap job will scaffold it from template-projects. Call propose_scaffold.' }
             else { 'This is an existing git repo on GitHub, cloned for you; read it with read_file, list_dir and grep.' }
    $s = [ordered]@{ id = $id; target = $req.target; text = $req.text.Trim(); newTarget = $new; checkout = $checkout; status = 'idle'; error = $null
                     model = (Get-IdeaModel); costUsd = 0; scaffold = $null; idea = $null; bootstrap = $null
                     pending = $null; pendingResults = @(); specs = @(); createdAt = [DateTime]::UtcNow.ToString('o') }
    Add-Human $id 'idea' $s.text @{ target = $s.target; newTarget = $new }
    Save-Chat $id @([ordered]@{ role = 'user'; content = @([ordered]@{ type = 'text'; text = "Target: $($req.target ?? '(none yet)')`n$about`n`nIdea: $($s.text)" }) })
    Start-Turn $s
    Send-Result $ctx ([pscustomobject]@{ Ok = $true; Message = $id })
}

function Handle($ctx) {
    $method = $ctx.Request.HttpMethod
    $path = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath)

    if ($path -match '\.\.') { return Send $ctx 400 '{"ok":false,"message":"bad path"}' }

    if ($method -eq 'GET' -and ($path -eq '/' -or $path -eq '/index.html')) { return Send-File $ctx $Index }

    if ($method -eq 'GET' -and $path -like '/state/*') {
        $full = [IO.Path]::GetFullPath((Join-Path $StateDir $path.Substring(7)))
        if (-not $full.StartsWith($StateDir, [StringComparison]::OrdinalIgnoreCase)) { return Send $ctx 400 '{"ok":false,"message":"bad path"}' }
        return Send-File $ctx $full
    }

    if ($method -eq 'GET' -and $path -like '/vendor/*') {
        $full = [IO.Path]::GetFullPath((Join-Path $Vendor $path.Substring(8)))
        if (-not $full.StartsWith($Vendor, [StringComparison]::OrdinalIgnoreCase)) { return Send $ctx 400 '{"ok":false,"message":"bad path"}' }
        return Send-File $ctx $full
    }

    if ($method -eq 'GET' -and $path -match '^/api/agents/([A-Za-z0-9_-]+)/transcript$') {
        $agentId = $Matches[1]
        $events = Join-Path $StateDir 'events.jsonl'
        $start = if (Test-Path $events) {
            Get-Content $events | Where-Object { $_ -match '"agent-start"' -and $_ -match [regex]::Escape("`"agentId`":`"$agentId`"") } |
                Select-Object -Last 1 | ConvertFrom-Json
        }
        if (-not $start -or -not $start.PSObject.Properties['transcript']) { return Send $ctx 404 '{"ok":false,"message":"no such agent"}' }
        return Send-File $ctx $start.transcript
    }

    if ($method -eq 'GET' -and $path -match '^/api/jobs/([A-Z]+-\d+)/files$') {
        $dir = Join-Path $StateDir "jobs/$($Matches[1])"
        $files = @()
        if (Test-Path $dir) {
            $files = @(Get-ChildItem $dir -Recurse -File | ForEach-Object {
                [IO.Path]::GetRelativePath($dir, $_.FullName) -replace '\\', '/'
            } | Sort-Object)
        }
        return Send $ctx 200 (ConvertTo-Json -Compress -InputObject $files)
    }

    if ($method -eq 'PUT' -and $path -match '^/api/jobs/([A-Z]+-\d+)/spec$') {
        $tmp = Join-Path $StateDir "spec-edit-$PID.md"
        try {
            Set-Content $tmp (Read-Body $ctx) -NoNewline
            $r = Invoke-Factory edit-spec $Matches[1] -File $tmp
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
        if ($r.Ok) { $r.Message = 'spec saved' }
        return Send-Result $ctx $r
    }

    if ($method -eq 'POST' -and $path -match '^/api/jobs/([A-Z]+-\d+)/provider$') {
        $id = $Matches[1]
        $req = Read-Body $ctx | ConvertFrom-Json
        return Send-Result $ctx (Invoke-Factory set-provider $id "$($req.provider)")
    }

    if ($method -eq 'POST' -and $path -match '^/api/jobs/([A-Z]+-\d+)/move$') {
        $id = $Matches[1]
        $req = Read-Body $ctx | ConvertFrom-Json
        $to = $req.to
        $note = if ($req.PSObject.Properties['note']) { $req.note } else { $null }
        $job = (Read-Board).jobs | Where-Object id -eq $id
        if (-not $job) { return Send $ctx 404 '{"ok":false,"message":"no such job"}' }
        $r = switch ($to) {
            'approved' {
                if ($job.stage -eq 'ready') {
                    if (-not $note) { [pscustomobject]@{ Ok = $false; Message = 'rework refused: a rework needs a note' } }
                    else { Invoke-Factory rework $id $note }
                } else { Invoke-Factory approve $id }
            }
            'done' { Invoke-Factory merge $id }
            'discarded' { Invoke-Factory discard $id }
            default { [pscustomobject]@{ Ok = $false; Message = "move to '$to' refused: not a human move" } }
        }
        if ($r.Ok -and $to -eq 'approved') { Start-Detached 'runner.ps1' }
        return Send-Result $ctx $r
    }

    # Tear down: destroying takes minutes, so it runs detached and the card follows the board.
    if ($method -eq 'POST' -and $path -match '^/api/jobs/([A-Z]+-\d+)/destroy$') {
        Start-Detached 'factory.ps1' @('destroy', $Matches[1])
        return Send-Result $ctx ([pscustomobject]@{ Ok = $true; Message = 'tearing down the preview' })
    }

    if ($method -eq 'GET' -and $path -match '^/api/jobs/([A-Z]+-\d+)/qa-login$') {
        return Send-Result $ctx (Invoke-Factory qa-login $Matches[1])
    }

    if ($method -eq 'GET' -and $path -eq '/api/lessons') {
        $dir = Join-Path $StateDir 'lessons'
        $list = @(if (Test-Path $dir) {
            Get-ChildItem $dir -Filter '*.json' -File | Where-Object Name -match '^[A-Z]+-(\d+)\.json$' |
                Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | ForEach-Object { Read-Json $_.FullName }
        })
        return Send $ctx 200 (ConvertTo-Json -Compress -Depth 10 -InputObject $list)
    }

    if ($method -eq 'POST' -and $path -match '^/api/lessons/([A-Z]+-\d+)/mark$') {
        $id = $Matches[1]
        $req = Read-Body $ctx | ConvertFrom-Json
        $session = if ($req.PSObject.Properties['session'] -and $req.session) { @('-Detail', "$($req.session)") } else { @() }
        return Send-Result $ctx (Invoke-Factory lesson-mark $id "$($req.status)" @session)
    }

    if ($method -eq 'GET' -and $path -eq '/api/ideate/config') {
        return Send $ctx 200 (@{ enabled = [bool](Get-EnvValue 'ANTHROPIC_API_KEY'); model = (Get-IdeaModel); owner = (Get-EnvValue 'GITHUB_OWNER')
                                 templatesRepo = (Get-EnvValue 'FACTORY_TEMPLATES_REPO') } | ConvertTo-Json -Compress)
    }

    if ($method -eq 'GET' -and $path -eq '/api/repos') {
        $r = Invoke-Factory list-repos
        if (-not $r.Ok) { return Send-Result $ctx $r }
        return Send $ctx 200 $r.Message
    }

    if ($method -eq 'GET' -and $path -eq '/api/sessions') {
        $list = @(if (Test-Path $Sessions) {
            Get-ChildItem $Sessions -Directory | Sort-Object Name | ForEach-Object {
                $f = Join-Path $_.FullName 'session.json'
                if (Test-Path $f) { $x = Read-Json $f; if ($x.status -ne 'closed') {
                    [ordered]@{ id = $x.id; target = $x.target; text = $x.text; status = $x.status; idea = $x.idea; bootstrap = $x.bootstrap; costUsd = $x.costUsd; newTarget = $x.newTarget } } }
            }
        })
        return Send $ctx 200 (ConvertTo-Json -Compress -Depth 5 -InputObject $list)
    }

    if ($method -eq 'GET' -and $path -eq '/api/evals') {
        $dir = Join-Path $StateDir 'evals'
        $list = @(if (Test-Path $dir) {
            Get-ChildItem $dir -Filter '*.json' -File | Where-Object Name -match '^[A-Z]+-(\d+)\.json$' |
                Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | ForEach-Object { Read-Json $_.FullName }
        })
        return Send $ctx 200 (ConvertTo-Json -Compress -Depth 10 -InputObject $list)
    }

    if ($method -eq 'PUT' -and $path -match '^/api/evals/([A-Z]+-\d+)/feedback$') {
        $tmp = Join-Path $StateDir "feedback-$PID.md"
        try {
            Set-Content $tmp (Read-Body $ctx) -NoNewline
            $r = Invoke-Factory feedback $Matches[1] -File $tmp
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
        if ($r.Ok) { $r.Message = 'feedback saved' }
        return Send-Result $ctx $r
    }

    if ($method -eq 'POST' -and $path -eq '/api/sessions') { return New-Session $ctx }

    if ($path -match '^/api/sessions/(S-\d+)/(reply|brief|write-specs|close)$' -and $method -eq 'POST') { return Handle-Session $ctx $method $Matches[1] $Matches[2] $null }
    if ($path -match '^/api/sessions/(S-\d+)/specs/(\d+)$' -and $method -in 'POST', 'PUT') { return Handle-Session $ctx $method $Matches[1] 'specs' $Matches[2] }


    if ($method -eq 'POST' -and $path -eq '/api/purge') {
        return Send-Result $ctx (Invoke-Factory purge)
    }

    Send $ctx 404 '{"ok":false,"message":"not found"}'
}

$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "factory dashboard on http://localhost:$Port/  (state: $StateDir)"
try {
    while ($listener.IsListening) {
        $task = $listener.GetContextAsync()
        while (-not $task.AsyncWaitHandle.WaitOne(250)) { Invoke-Watchdog; Invoke-PrSync }
        $ctx = $task.GetAwaiter().GetResult()
        try { Handle $ctx }
        catch {
            Write-Warning "$($ctx.Request.HttpMethod) $($ctx.Request.Url.AbsolutePath): $_"
            try { Send $ctx 500 (@{ ok = $false; message = "$_" } | ConvertTo-Json -Compress) } catch { }
        }
    }
} finally {
    $listener.Stop()
}
