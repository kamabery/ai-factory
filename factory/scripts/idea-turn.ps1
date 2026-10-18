<#
.SYNOPSIS
  Runs one assistant turn of a dashboard idea session against the Claude API. See "Idea sessions" in
  docs/FACTORY-CONTRACT.md.
.DESCRIPTION
  serve.ps1 starts it detached. It holds sessions/<S>/turn.lock for the turn, streams the reply into
  stream.jsonl, runs the read-only and spec tools itself, and stops when the model stops or calls a UI
  tool. The API key is read from the env file into a local variable. It is never set in the
  environment, so a claude CLI started later keeps using the subscription.
#>
param(
    [Parameter(Mandatory)] [string] $Session,
    [string] $StateDir,
    [string] $EnvFile
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $StateDir) { $StateDir = if ($env:FACTORY_HOME) { $env:FACTORY_HOME } else { Join-Path $Repo '.factory-runs' } }
if (-not $EnvFile) { $EnvFile = Join-Path $Repo '.env' }
$StateDir = [IO.Path]::GetFullPath($StateDir)
$Dir = Join-Path $StateDir "sessions/$Session"
$Templates = Join-Path $Repo 'factory/templates'

$DefaultModel = 'claude-opus-5-5'
# USD per million tokens: input, output, cache read, cache write (5-minute TTL, 1.25x input).
$Prices = @{
    'claude-opus-5-5'   = @{ in = 4.0; out = 20.0; read = 0.20; write = 5.0 }
    'claude-sonnet-5-5' = @{ in = 2.0; out = 10.0; read = 0.20; write = 2.5 }
}
$UiTools = 'ask_questions', 'propose_scaffold', 'submit_brief'
$Skip = '.git', 'node_modules', 'bin', 'obj', '.vs'
$MaxFile = 256KB
$MaxLoops = 40

# --- files ------------------------------------------------------------------------------------

function Read-Json([string] $path) { Get-Content $path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -DateKind String }
# Atomic: a temp file, then a rename. The rename is retried while a reader (serve.ps1 sending the
# file to the dashboard) holds the target open, which Windows refuses as access denied.
function Write-Json([string] $path, $o) {
    $tmp = "$path.$PID.tmp"
    [IO.File]::WriteAllText($tmp, ($o | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
    for ($i = 0; ; $i++) {
        try { [IO.File]::Move($tmp, $path, $true); return }
        catch [UnauthorizedAccessException], [IO.IOException] { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 50 }
    }
}
function Add-Stream([hashtable] $o) {
    [IO.File]::AppendAllText((Join-Path $Dir 'stream.jsonl'), ($o | ConvertTo-Json -Compress -Depth 10) + "`n", [Text.UTF8Encoding]::new($false))
}
function New-List($items) { $l = [Collections.Generic.List[object]]::new(); foreach ($x in @($items)) { if ($null -ne $x) { $l.Add($x) } }; , $l }

function Read-DotEnv([string] $path) {
    $vars = @{}
    if (-not (Test-Path $path)) { return $vars }
    foreach ($line in Get-Content $path -Encoding utf8) {
        if ($line -match '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$' -and $line -notmatch '^\s*#') {
            $vars[$Matches[1]] = $Matches[2] -replace '^"(.*)"$', '$1' -replace "^'(.*)'$", '$1'
        }
    }
    $vars
}

# --- the turn lock (the same takeover rule as runner.lock) -------------------------------------

function Enter-Lock {
    $lock = Join-Path $Dir 'turn.lock'
    for ($try = 0; $try -lt 2; $try++) {
        try {
            $fs = [IO.File]::Open($lock, 'CreateNew', 'Write')
            $b = [Text.Encoding]::ASCII.GetBytes("$PID")
            $fs.Write($b, 0, $b.Length); $fs.Dispose()
            return $true
        } catch [IO.IOException] {
            $holder = (Get-Content $lock -ErrorAction SilentlyContinue | Select-Object -First 1) -as [int]
            if ($holder -and (Get-Process -Id $holder -ErrorAction SilentlyContinue)) { return $false }
            Remove-Item $lock -Force -ErrorAction SilentlyContinue
        }
    }
    $false
}

# --- the prompt and the tools -----------------------------------------------------------------

function Body-Of([string] $path) { (Get-Content $path -Raw -Encoding utf8) -replace '(?s)^---\r?\n.*?\r?\n---\r?\n', '' }

function Get-SystemPrompt {
    @(
        Body-Of (Join-Path $Repo 'factory/plugin/prompts/idea-api.md')
        "# The grill skill`n"
        Body-Of (Join-Path $Repo 'factory/plugin/skills/grill/SKILL.md')
        "# The spec-writer's rules`n"
        Body-Of (Join-Path $Repo 'factory/plugin/agents/spec-writer.md')
        "# The brief template (factory/templates/brief.md)`n"
        Get-Content (Join-Path $Templates 'brief.md') -Raw -Encoding utf8
        "# The spec template (factory/templates/spec.md)`n"
        Get-Content (Join-Path $Templates 'spec.md') -Raw -Encoding utf8
    ) -join "`n"
}

function Tool([string] $name, [string] $description, [hashtable] $props, [string[]] $required) {
    [ordered]@{ name = $name; description = $description; eager_input_streaming = $true
                input_schema = [ordered]@{ type = 'object'; properties = $props; required = $required } }
}

function Get-Tools {
    $spec = @{ type = 'object'; properties = @{ k = @{ type = 'integer' }; markdown = @{ type = 'string' } }; required = @('k', 'markdown') }
    $option = @{ type = 'object'; properties = @{ label = @{ type = 'string' }; description = @{ type = 'string' } }; required = @('label', 'description') }
    $question = @{ type = 'object'; required = @('question', 'header', 'options', 'multiSelect'); properties = @{
        question = @{ type = 'string' }; header = @{ type = 'string'; description = 'A chip label, at most 12 characters' }
        multiSelect = @{ type = 'boolean' }; options = @{ type = 'array'; items = $option; description = '2-4 options; the panel adds Other' } } }
    @(
        Tool 'read_file' 'Read a text file in the target repo (or factory/templates). A relative path is taken from the target root.' @{ path = @{ type = 'string' } } @('path')
        Tool 'list_dir' 'List one folder of the target repo (default: its root). Folders end in /.' @{ path = @{ type = 'string' } } @()
        Tool 'grep' 'Search file contents under the target repo with a .NET regex. Prints path:line: text, at most 200 lines.' @{
            pattern = @{ type = 'string' }; path = @{ type = 'string'; description = 'folder to search, default the target root' }
            glob = @{ type = 'string'; description = 'file name filter such as *.cs' } } @('pattern')
        Tool 'submit_specs' 'Write the specs for the confirmed brief: 1-5 of them, k = 1..N, each the full markdown starting with "# Spec: <title>". Only after the brief is confirmed.' @{
            specs = @{ type = 'array'; items = $spec } } @('specs')
        Tool 'revise_spec' 'Replace spec k (not yet decided by the human) with new full markdown.' @{ k = @{ type = 'integer' }; markdown = @{ type = 'string' } } @('k', 'markdown')
        Tool 'ask_questions' 'Ask the human 1-4 questions in one round, each with 2-4 options. Ends your turn; the answers come back as the result.' @{
            questions = @{ type = 'array'; items = $question } } @('questions')
        Tool 'propose_scaffold' 'New target only: propose its scaffold options; the human confirms or edits them in a form. Ends your turn.' @{
            name = @{ type = 'string'; description = 'lower-case letters and digits, starting with a letter' }
            resource = @{ type = 'string'; description = 'PascalCase: the main thing the app manages' }
            db = @{ type = 'string'; enum = @('sqlite', 'sqlserver', 'postgres') }
            ui = @{ type = 'string'; enum = @('react', 'blazor', 'blazor-maui', 'none') }
            events = @{ type = 'boolean' } } @('name', 'resource', 'db', 'ui', 'events')
        Tool 'submit_brief' 'Show the human the whole brief for confirmation. Ends your turn; a correction comes back as the result.' @{ markdown = @{ type = 'string' } } @('markdown')
    )
}

# --- server-side tools ------------------------------------------------------------------------

function Roots {
    $r = @()
    if ($S.target -and (Test-Path $S.target -PathType Container)) { $r += [IO.Path]::GetFullPath($S.target) }
    $r + [IO.Path]::GetFullPath($Templates)
}

# Resolves a model-supplied path and refuses anything outside the target and the templates.
function Resolve-Fenced([string] $path) {
    $roots = Roots
    $base = $roots[0]
    $full = if (-not $path) { $base } elseif ([IO.Path]::IsPathFullyQualified($path)) { [IO.Path]::GetFullPath($path) } else { [IO.Path]::GetFullPath((Join-Path $base $path)) }
    foreach ($root in $roots) {
        $r = $root.TrimEnd('\', '/')
        if ($full -eq $r -or $full.StartsWith($r + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $full }
    }
    throw "'$path' is outside the target and factory/templates"
}

function Rel([string] $full) { foreach ($root in Roots) { if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return [IO.Path]::GetRelativePath($root, $full) -replace '\\', '/' } }; $full }

function Get-Files([string] $root) {
    Get-ChildItem -LiteralPath $root -Force | ForEach-Object {
        if ($_.PSIsContainer) { if ($_.Name -notin $Skip) { Get-Files $_.FullName } } else { $_ }
    }
}

function Spec-Title([string] $md) { if ($md -match '(?m)^#\s*Spec:\s*(.+?)\s*$') { $Matches[1] } }

function Invoke-ServerTool($tu) {
    $in = $tu.input
    switch ($tu.name) {
        'read_file' {
            $p = Resolve-Fenced $in.path
            if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "no file '$($in.path)'" }
            if ((Get-Item -LiteralPath $p).Length -gt $MaxFile) { throw "'$($in.path)' is larger than $($MaxFile / 1KB) KB; use grep" }
            return Get-Content -LiteralPath $p -Raw -Encoding utf8
        }
        'list_dir' {
            $p = Resolve-Fenced $in.path
            if (-not (Test-Path -LiteralPath $p -PathType Container)) { throw "no folder '$($in.path)'" }
            $items = Get-ChildItem -LiteralPath $p -Force | Where-Object { $_.Name -notin $Skip } | Sort-Object { -not $_.PSIsContainer }, Name |
                ForEach-Object { if ($_.PSIsContainer) { "$($_.Name)/" } else { $_.Name } }
            return (@($items) -join "`n")
        }
        'grep' {
            $p = Resolve-Fenced $in.path
            $files = if (Test-Path -LiteralPath $p -PathType Leaf) { Get-Item -LiteralPath $p } else { Get-Files $p }
            if ($in.glob) { $files = $files | Where-Object Name -like $in.glob }
            $hits = @($files | Select-String -Pattern $in.pattern -ErrorAction SilentlyContinue | Select-Object -First 201 |
                ForEach-Object { "$(Rel $_.Path):$($_.LineNumber): $($_.Line.Trim())" })
            if (-not $hits) { return 'no matches' }
            if ($hits.Count -gt 200) { $hits = $hits[0..199] + '(more matches not shown; narrow the search)' }
            return ($hits -join "`n")
        }
        'submit_specs' {
            if (-not $S.idea) { throw 'the brief is not confirmed yet; submit_specs comes after the human confirms it' }
            if (@($S.specs | Where-Object { $_.decision })) { throw 'the human has started deciding specs; use revise_spec' }
            $specs = @($in.specs)
            if ($specs.Count -lt 1 -or $specs.Count -gt 5) { throw 'submit 1 to 5 specs' }
            foreach ($sp in $specs) { if (-not (Spec-Title $sp.markdown)) { throw "spec $($sp.k) has no '# Spec: <title>' line" } }
            $ideaDir = New-Item -ItemType Directory -Force (Join-Path $StateDir "ideas/$($S.idea)")
            $list = New-List @()
            foreach ($sp in $specs) {
                Set-Content (Join-Path $ideaDir "spec-$($sp.k).md") $sp.markdown -Encoding utf8
                $list.Add([ordered]@{ k = [int]$sp.k; title = (Spec-Title $sp.markdown); decision = $null; job = $null })
            }
            $S.specs = $list
            return "wrote $($specs.Count) spec(s); the human now decides each one in the panel"
        }
        'revise_spec' {
            $entry = @($S.specs) | Where-Object { $_.k -eq [int]$in.k } | Select-Object -First 1
            if (-not $entry) { throw "there is no spec $($in.k)" }
            if ($entry.decision) { throw "spec $($in.k) is already decided ($($entry.decision))" }
            if (-not (Spec-Title $in.markdown)) { throw "the new markdown has no '# Spec: <title>' line" }
            Set-Content (Join-Path $StateDir "ideas/$($S.idea)/spec-$($in.k).md") $in.markdown -Encoding utf8
            $entry.title = Spec-Title $in.markdown
            return "spec $($in.k) revised"
        }
    }
    throw "unknown tool '$($tu.name)'"
}

function Test-UiInput($tu) {
    $in = $tu.input
    switch ($tu.name) {
        'ask_questions' {
            $q = @($in.questions)
            if ($q.Count -lt 1 -or $q.Count -gt 4) { return 'ask 1 to 4 questions' }
            foreach ($x in $q) { if (-not $x.question -or @($x.options).Count -lt 2) { return 'every question needs text and at least 2 options' } }
        }
        'submit_brief' { if (-not $in.markdown) { return 'markdown is required' } }
        'propose_scaffold' { foreach ($k in 'name', 'resource', 'db', 'ui') { if (-not $in.$k) { return "$k is required" } } }
    }
    $null
}

function Summary($tu) { "$($tu.input.path ?? $tu.input.pattern ?? '')".Trim() }

# --- the Messages API -------------------------------------------------------------------------

function Get-ApiError([string] $body, [int] $code) {
    try { $o = $body | ConvertFrom-Json -AsHashtable; "API $code ($($o.error.type)): $($o.error.message)" } catch { "API $code`: $body" }
}

# Streams one request. Returns @{ content; stop_reason; usage; stop_details }. Text is appended to
# stream.jsonl as it arrives. Each block is kept as sent (thinking blocks with their signatures), so the
# next request can pass it back unchanged.
function Invoke-Messages([hashtable] $body) {
    $json = $body | ConvertTo-Json -Depth 100 -Compress
    for ($attempt = 0; ; $attempt++) {
        $req = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$BaseUrl/v1/messages")
        $req.Headers.Add('x-api-key', $Key)
        $req.Headers.Add('anthropic-version', '2023-06-01')
        $req.Headers.Add('anthropic-beta', 'server-side-fallback-2026-07-01')
        $req.Content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
        $resp = $Http.SendAsync($req, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $code = [int]$resp.StatusCode
        if ($resp.IsSuccessStatusCode) { break }
        $err = Get-ApiError ($resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) $code
        if ($attempt -ge 2 -or $code -notin 429, 500, 502, 503, 504, 529) { throw $err }
        $wait = ($resp.Headers.RetryAfter.Delta.TotalSeconds) ?? (3 * ($attempt + 1))
        Add-Stream @{ type = 'tool'; name = 'retry'; summary = "$err; retrying in $([int]$wait)s" }
        Start-Sleep -Seconds ([Math]::Min(30, [int]$wait))
    }

    $blocks = [Collections.Generic.SortedDictionary[int, object]]::new()
    $partial = @{}
    $out = @{ stop_reason = $null; usage = @{}; stop_details = $null }
    $reader = [IO.StreamReader]::new($resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult(), [Text.Encoding]::UTF8)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if (-not $line.StartsWith('data:')) { continue }
            $e = $line.Substring(5).Trim() | ConvertFrom-Json -AsHashtable
            switch ($e.type) {
                'message_start' { $out.usage = $e.message.usage ?? @{} }
                'content_block_start' { $blocks[[int]$e.index] = $e.content_block }
                'content_block_delta' {
                    $b = $blocks[[int]$e.index]; $d = $e.delta
                    switch ($d.type) {
                        'text_delta' { $b.text += $d.text; Add-Stream @{ type = 'text'; text = $d.text } }
                        'input_json_delta' { $partial[[int]$e.index] += $d.partial_json }
                        'thinking_delta' { $b.thinking += $d.thinking }
                        'signature_delta' { $b.signature = $d.signature }
                    }
                }
                'content_block_stop' {
                    $b = $blocks[[int]$e.index]
                    if ($b.type -eq 'tool_use') {
                        $raw = $partial[[int]$e.index]
                        $b.input = [ordered]@{}
                        if ($raw) { try { $b.input = $raw | ConvertFrom-Json -AsHashtable } catch { $b['_invalid'] = $raw } }
                    }
                }
                'message_delta' {
                    if ($e.delta.stop_reason) { $out.stop_reason = $e.delta.stop_reason }
                    if ($e.delta.Contains('stop_details')) { $out.stop_details = $e.delta.stop_details }
                    if ($e.usage) { foreach ($k in $e.usage.Keys) { $out.usage[$k] = $e.usage[$k] } }
                }
                'error' { throw "API stream error ($($e.error.type)): $($e.error.message)" }
            }
        }
    } finally { $reader.Dispose(); $resp.Dispose() }
    $out.content = New-List $blocks.Values
    $out
}

function Get-Cost($usage) {
    $p = $Prices[$Model]
    if (-not $p) { return 0 }
    (($usage.input_tokens ?? 0) * $p.in + ($usage.output_tokens ?? 0) * $p.out +
     ($usage.cache_read_input_tokens ?? 0) * $p.read + ($usage.cache_creation_input_tokens ?? 0) * $p.write) / 1e6
}

# --- the turn ---------------------------------------------------------------------------------

if (-not (Test-Path (Join-Path $Dir 'session.json'))) { [Console]::Error.WriteLine("idea-turn: no session $Session"); exit 2 }
if (-not (Enter-Lock)) { [Console]::Error.WriteLine("idea-turn: a turn is already running for $Session"); exit 3 }

$S = Read-Json (Join-Path $Dir 'session.json')
$chat = Read-Json (Join-Path $Dir 'chat.json')
$messages = New-List $chat.messages
$vars = Read-DotEnv $EnvFile
$Key = $vars['ANTHROPIC_API_KEY']
$Model = if ($vars['FACTORY_IDEA_MODEL']) { $vars['FACTORY_IDEA_MODEL'] } else { $DefaultModel }
$BaseUrl = if ($vars['ANTHROPIC_BASE_URL']) { $vars['ANTHROPIC_BASE_URL'].TrimEnd('/') } else { 'https://api.anthropic.com' }
$cost = 0.0

$S.status = 'running'; $S.error = $null; $S.model = $Model
Write-Json (Join-Path $Dir 'session.json') $S
[IO.File]::WriteAllText((Join-Path $Dir 'stream.jsonl'), '')

try {
    if (-not $Key) { throw "no ANTHROPIC_API_KEY in $EnvFile" }
    $Http = [Net.Http.HttpClient]::new()
    $Http.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $system = @(@{ type = 'text'; text = (Get-SystemPrompt); cache_control = @{ type = 'ephemeral' } })
    $tools = Get-Tools
    $S.pending = $null; $S.pendingResults = @()

    for ($loop = 0; $loop -lt $MaxLoops; $loop++) {
        $r = Invoke-Messages ([ordered]@{
            model = $Model; max_tokens = 64000; stream = $true
            system = $system; tools = $tools; messages = $messages
            cache_control = @{ type = 'ephemeral' }
            output_config = @{ effort = 'high' }
            fallbacks = 'default'
        })
        $cost += Get-Cost $r.usage
        if ($r.stop_reason -eq 'refusal') {
            throw "the model declined to continue$(if ($r.stop_details.category) { " ($($r.stop_details.category))" })"
        }
        # A tool input that streamed as invalid JSON is answered with an error, never run.
        $invalid = @{}
        foreach ($b in $r.content) { if ($b.Contains('_invalid')) { $invalid[$b.id] = $true; $b.Remove('_invalid') } }
        $messages.Add([ordered]@{ role = 'assistant'; content = $r.content })
        if ($r.stop_reason -eq 'max_tokens') { throw 'the reply hit max_tokens' }
        $uses = @($r.content | Where-Object { $_.type -eq 'tool_use' })
        if ($r.stop_reason -ne 'tool_use' -or -not $uses) { break }

        $results = New-List @()
        $ui = $null
        foreach ($tu in $uses) {
            $res = [ordered]@{ type = 'tool_result'; tool_use_id = $tu.id }
            try {
                if ($invalid[$tu.id]) { throw 'the tool input was not valid JSON; call the tool again' }
                if ($tu.name -in $UiTools) {
                    if ($ui) { throw 'only one UI tool per turn; call it again in your next turn' }
                    $bad = Test-UiInput $tu
                    if ($bad) { throw $bad }
                    $ui = $tu
                    continue
                }
                Add-Stream @{ type = 'tool'; name = $tu.name; summary = (Summary $tu) }
                $res.content = Invoke-ServerTool $tu
            } catch {
                $res.content = "$($_.Exception.Message)"; $res.is_error = $true
            }
            $results.Add($res)
        }
        if ($ui) {
            $S.pending = [ordered]@{ id = $ui.id; name = $ui.name; input = $ui.input }
            $S.pendingResults = $results
            break
        }
        $messages.Add([ordered]@{ role = 'user'; content = $results })
    }
    $S.status = 'idle'
} catch {
    $S.status = 'error'
    $S.error = "$($_.Exception.Message)"
    Add-Stream @{ type = 'error'; message = $S.error }
} finally {
    $S.costUsd = [double]($S.costUsd ?? 0) + $cost
    Write-Json (Join-Path $Dir 'chat.json') ([ordered]@{ messages = $messages })
    Write-Json (Join-Path $Dir 'session.json') $S
    Add-Stream @{ type = 'end' }
    Remove-Item (Join-Path $Dir 'turn.lock') -Force -ErrorAction SilentlyContinue
}
exit 0
