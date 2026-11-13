<#
.SYNOPSIS
  A stand-in for `claude -p ... --output-format stream-json` in an idea turn (FACTORY_IDEA_CLAUDE_CMD), so idea
  sessions can be tested without a token.
.DESCRIPTION
  Takes claude's arguments and the prompt on stdin. It starts the MCP server named in --mcp-config, as claude would,
  and keeps each session's conversation in <FACTORY_IDEA_STUB_STATE>/<session>.json, so --resume continues it. Each
  model reply comes from anthropic-stub.ps1 (FACTORY_IDEA_STUB_URL), which serves scripted replies in order and saves
  every request: the request is the Messages API body claude would send (model, system, tools, messages), with the
  tools named mcp__factory__<name>. Every tool_use runs through the MCP server, and the loop ends when a reply stops for
  anything but tool_use. Prints stream-json as claude does: system init, stream_event text deltas, assistant and user
  messages, and a result with total_cost_usd at the model's list prices.
#>
$ErrorActionPreference = 'Stop'
$Prices = @{
    'claude-opus-5-5'   = @{ in = 4.0; out = 20.0; read = 0.20; write = 5.0 }
    'claude-sonnet-5-5' = @{ in = 2.0; out = 10.0; read = 0.20; write = 2.5 }
}

function Out-Line($o) { [Console]::Out.WriteLine(($o | ConvertTo-Json -Compress -Depth 30)); [Console]::Out.Flush() }

# claude's arguments: --name value pairs and a few flags.
$opt = @{}
for ($i = 0; $i -lt $args.Count; $i++) {
    $a = "$($args[$i])"
    if ($a -in '-p', '--verbose', '--include-partial-messages', '--strict-mcp-config') { $opt[$a] = $true; continue }
    # pwsh -File may drop an empty argument (--tools ""), so a value that is itself a flag means an empty value.
    if ($a.StartsWith('--')) {
        if ($i + 1 -lt $args.Count -and -not "$($args[$i + 1])".StartsWith('--')) { $opt[$a] = "$($args[$i + 1])"; $i++ } else { $opt[$a] = '' }
    }
}
$model = $opt['--model']
$session = if ($opt['--session-id']) { $opt['--session-id'] } else { $opt['--resume'] }
$stateDir = if ($env:FACTORY_IDEA_STUB_STATE) { $env:FACTORY_IDEA_STUB_STATE } else { Join-Path ([IO.Path]::GetTempPath()) 'claude-stub' }
New-Item -ItemType Directory -Force $stateDir | Out-Null
$convo = Join-Path $stateDir "$session.json"
$prompt = [Console]::In.ReadToEnd()
# What this claude was given, for the tests: its arguments, its prompt, and the provider variables it saw.
$seen = [ordered]@{ args = @($args); prompt = $prompt; cwd = (Get-Location).Path; env = [ordered]@{} }
foreach ($n in 'ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_BASE_URL', 'CLAUDE_CODE_USE_FOUNDRY', 'ANTHROPIC_FOUNDRY_RESOURCE',
               'ANTHROPIC_DEFAULT_OPUS_MODEL', 'ANTHROPIC_DEFAULT_SONNET_MODEL', 'ANTHROPIC_DEFAULT_HAIKU_MODEL') {
    $seen.env[$n] = [Environment]::GetEnvironmentVariable($n)
}
$seen | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $stateDir 'last-run.json') -Encoding utf8

function Fail([string] $msg) {
    Out-Line @{ type = 'result'; subtype = 'error_during_execution'; is_error = $true; result = $msg; session_id = $session; total_cost_usd = 0 }
    exit 1
}

$messages = [Collections.Generic.List[object]]::new()
if ($opt['--resume']) {
    if (-not (Test-Path $convo)) { Fail "No conversation found with session ID: $session" }
    foreach ($m in (Get-Content $convo -Raw | ConvertFrom-Json -AsHashtable).messages) { $messages.Add($m) }
}
$messages.Add([ordered]@{ role = 'user'; content = @([ordered]@{ type = 'text'; text = $prompt }) })

# --- the MCP server, as claude starts it --------------------------------------------------------
$cfg = (Get-Content $opt['--mcp-config'] -Raw | ConvertFrom-Json -AsHashtable).mcpServers.factory
$psi = [Diagnostics.ProcessStartInfo]::new($cfg.command)
foreach ($x in $cfg.args) { $psi.ArgumentList.Add($x) }
foreach ($k in $cfg.env.Keys) { $psi.Environment[$k] = $cfg.env[$k] }
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true
$psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$mcp = [Diagnostics.Process]::Start($psi)
$script:rpc = 0
function Rpc([string] $method, $params) {
    $script:rpc++
    $mcp.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = $script:rpc; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 30))
    $mcp.StandardInput.Flush()
    ($mcp.StandardOutput.ReadLine() | ConvertFrom-Json -AsHashtable).result
}
$null = Rpc 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'claude-stub'; version = '0' } }
$mcp.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$tools = @((Rpc 'tools/list' @{}).tools | ForEach-Object { [ordered]@{ name = "mcp__factory__$($_.name)"; description = $_.description; input_schema = $_.inputSchema } })
Out-Line @{ type = 'system'; subtype = 'init'; session_id = $session; model = $model; tools = @($tools | ForEach-Object name) }

# --- the model, through anthropic-stub.ps1 ------------------------------------------------------
$Http = [Net.Http.HttpClient]::new()
$system = Get-Content $opt['--system-prompt-file'] -Raw -Encoding utf8
function Invoke-Model {
    $body = [ordered]@{ model = $model; max_tokens = 64000; stream = $true; system = $system; tools = $tools; messages = $messages } | ConvertTo-Json -Depth 100 -Compress
    $req = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$($env:FACTORY_IDEA_STUB_URL.TrimEnd('/'))/v1/messages")
    $req.Content = [Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
    $resp = $Http.SendAsync($req, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    if (-not $resp.IsSuccessStatusCode) {
        $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $msg = try { ($text | ConvertFrom-Json -AsHashtable).error.message } catch { $text }
        Fail "API Error: $([int]$resp.StatusCode) $msg"
    }
    $blocks = [Collections.Generic.SortedDictionary[int, object]]::new()
    $partial = @{}
    $out = @{ id = "msg_stub_$([guid]::NewGuid().ToString('N').Substring(0, 12))"; stop_reason = $null; usage = @{} }
    $reader = [IO.StreamReader]::new($resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult(), [Text.Encoding]::UTF8)
    while ($null -ne ($line = $reader.ReadLine())) {
        if (-not $line.StartsWith('data:')) { continue }
        $e = $line.Substring(5).Trim() | ConvertFrom-Json -AsHashtable
        switch ($e.type) {
            'message_start' { $out.usage = $e.message.usage ?? @{} }
            'content_block_start' { $blocks[[int]$e.index] = $e.content_block }
            'content_block_delta' {
                $b = $blocks[[int]$e.index]; $d = $e.delta
                switch ($d.type) {
                    'text_delta' { $b.text += $d.text; Out-Line @{ type = 'stream_event'; event = @{ type = 'content_block_delta'; index = $e.index; delta = @{ type = 'text_delta'; text = $d.text } } } }
                    'input_json_delta' { $partial[[int]$e.index] += $d.partial_json }
                    'thinking_delta' { $b.thinking += $d.thinking }
                    'signature_delta' { $b.signature = $d.signature }
                }
            }
            'content_block_stop' {
                $b = $blocks[[int]$e.index]
                if ($b.type -eq 'tool_use') { $b.input = if ($partial[[int]$e.index]) { $partial[[int]$e.index] | ConvertFrom-Json -AsHashtable } else { @{} } }
            }
            'message_delta' {
                if ($e.delta.stop_reason) { $out.stop_reason = $e.delta.stop_reason }
                if ($e.usage) { foreach ($k in $e.usage.Keys) { $out.usage[$k] = $e.usage[$k] } }
            }
        }
    }
    $reader.Dispose(); $resp.Dispose()
    $out.content = @($blocks.Values)
    $out
}

$cost = 0.0
$last = ''
for ($loop = 0; $loop -lt 40; $loop++) {
    $r = Invoke-Model
    $p = $Prices[$model]
    if ($p) { $u = $r.usage; $cost += (($u.input_tokens ?? 0) * $p.in + ($u.output_tokens ?? 0) * $p.out + ($u.cache_read_input_tokens ?? 0) * $p.read + ($u.cache_creation_input_tokens ?? 0) * $p.write) / 1e6 }
    if ($r.stop_reason -eq 'refusal') { Fail 'the model declined to continue' }
    foreach ($b in $r.content) { if ($b.type -eq 'tool_use' -and -not "$($b.name)".StartsWith('mcp__')) { $b.name = "mcp__factory__$($b.name)" } }
    # claude prints one assistant event per block, all with the message's id.
    foreach ($b in $r.content) { Out-Line @{ type = 'assistant'; message = @{ id = $r.id; role = 'assistant'; content = @($b) }; session_id = $session } }
    $messages.Add([ordered]@{ role = 'assistant'; content = $r.content })
    foreach ($b in $r.content) { if ($b.type -eq 'text') { $last = $b.text } }
    $uses = @($r.content | Where-Object type -eq 'tool_use')
    if ($r.stop_reason -ne 'tool_use' -or -not $uses) { break }
    $results = @()
    foreach ($tu in $uses) {
        $res = Rpc 'tools/call' @{ name = ($tu.name -replace '^mcp__factory__', ''); arguments = $tu.input }
        $block = [ordered]@{ type = 'tool_result'; tool_use_id = $tu.id; content = @($res.content); is_error = [bool]$res.isError }
        Out-Line @{ type = 'user'; message = @{ role = 'user'; content = @($block) }; session_id = $session }
        $results += $block
    }
    $messages.Add([ordered]@{ role = 'user'; content = $results })
    # A model that obeys "your turn is over" ends once a UI tool was shown, without asking for another reply.
    $shown = @($uses | Where-Object { ($_.name -replace '^mcp__factory__', '') -in 'ask_questions', 'propose_scaffold', 'submit_brief' } |
        Where-Object { $id = $_.id; -not ($results | Where-Object { $_.tool_use_id -eq $id -and $_.is_error }) })
    if ($shown) { break }
}

$mcp.StandardInput.Close(); $mcp.WaitForExit(5000) | Out-Null
@{ messages = $messages } | ConvertTo-Json -Depth 100 | Set-Content $convo -Encoding utf8
Out-Line @{ type = 'result'; subtype = 'success'; is_error = $false; result = $last; session_id = $session; total_cost_usd = $cost }
exit 0
