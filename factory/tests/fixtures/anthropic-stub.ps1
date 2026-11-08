<#
.SYNOPSIS
  A stand-in for POST /v1/messages (stream: true), so idea sessions can be tested without a token.
.DESCRIPTION
  Serves <Dir>/001.json, 002.json, ... in order, one per request, as Messages API server-sent events,
  and saves each request body as <Dir>/requests/NNN.json (with its x-api-key in NNN.key, and its
  Authorization, anthropic-beta and x-api-key headers in NNN.headers).
  A script file is { "content": [ blocks ], "stop_reason": "...", "usage": { ... } }, where a block is
  { type: text, text } | { type: thinking, thinking, signature } | { type: tool_use, id, name, input },
  or { "status": <code>, "body": { ... } } for an error. POST /__reset { "dir"? } starts over.
#>
param(
    [Parameter(Mandatory)] [int] $Port,
    [Parameter(Mandatory)] [string] $Dir,
    [int] $DelayMs = 0
)

$ErrorActionPreference = 'Stop'
$script:n = 0

function Write-Event($res, [string] $type, $data) {
    $json = $data | ConvertTo-Json -Compress -Depth 30
    $bytes = [Text.Encoding]::UTF8.GetBytes("event: $type`ndata: $json`n`n")
    $res.OutputStream.Write($bytes, 0, $bytes.Length)
    $res.OutputStream.Flush()
    if ($DelayMs) { Start-Sleep -Milliseconds $DelayMs }
}

function Chunks([string] $s, [int] $size) {
    if (-not $s) { return , @('') }
    , @(for ($i = 0; $i -lt $s.Length; $i += $size) { $s.Substring($i, [Math]::Min($size, $s.Length - $i)) })
}

function Send-Script($ctx, $script) {
    $res = $ctx.Response
    if ($script.Contains('status')) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($script.body | ConvertTo-Json -Compress -Depth 10))
        $res.StatusCode = $script.status
        $res.ContentType = 'application/json'
        $res.OutputStream.Write($bytes, 0, $bytes.Length)
        $res.Close()
        return
    }
    $res.StatusCode = 200
    $res.ContentType = 'text/event-stream'
    $res.SendChunked = $true
    $u = $script.usage ?? @{}
    Write-Event $res 'message_start' @{ type = 'message_start'; message = @{
        id = "msg_stub_$script:n"; type = 'message'; role = 'assistant'; content = @(); stop_reason = $null
        usage = @{ input_tokens = $u.input_tokens ?? 100; cache_creation_input_tokens = $u.cache_creation_input_tokens ?? 0
                   cache_read_input_tokens = $u.cache_read_input_tokens ?? 0; output_tokens = 1 } } }
    $i = 0
    foreach ($b in $script.content) {
        switch ($b.type) {
            'text' {
                Write-Event $res 'content_block_start' @{ type = 'content_block_start'; index = $i; content_block = @{ type = 'text'; text = '' } }
                foreach ($c in (Chunks $b.text 8)) { Write-Event $res 'content_block_delta' @{ type = 'content_block_delta'; index = $i; delta = @{ type = 'text_delta'; text = $c } } }
            }
            'thinking' {
                Write-Event $res 'content_block_start' @{ type = 'content_block_start'; index = $i; content_block = @{ type = 'thinking'; thinking = '' } }
                if ($b.thinking) { Write-Event $res 'content_block_delta' @{ type = 'content_block_delta'; index = $i; delta = @{ type = 'thinking_delta'; thinking = $b.thinking } } }
                Write-Event $res 'content_block_delta' @{ type = 'content_block_delta'; index = $i; delta = @{ type = 'signature_delta'; signature = $b.signature ?? 'sig' } }
            }
            'tool_use' {
                Write-Event $res 'content_block_start' @{ type = 'content_block_start'; index = $i; content_block = @{ type = 'tool_use'; id = $b.id; name = $b.name; input = @{} } }
                $json = $b.input | ConvertTo-Json -Compress -Depth 30
                foreach ($c in (Chunks $json ([Math]::Max(1, [Math]::Ceiling($json.Length / 2))))) {
                    Write-Event $res 'content_block_delta' @{ type = 'content_block_delta'; index = $i; delta = @{ type = 'input_json_delta'; partial_json = $c } }
                }
            }
        }
        Write-Event $res 'content_block_stop' @{ type = 'content_block_stop'; index = $i }
        $i++
    }
    Write-Event $res 'message_delta' @{ type = 'message_delta'; delta = @{ stop_reason = $script.stop_reason ?? 'end_turn'; stop_sequence = $null }
                                       usage = @{ output_tokens = $u.output_tokens ?? 50 } }
    Write-Event $res 'message_stop' @{ type = 'message_stop' }
    $res.Close()
}

function Read-Body($ctx) {
    $r = [IO.StreamReader]::new($ctx.Request.InputStream, [Text.Encoding]::UTF8)
    try { $r.ReadToEnd() } finally { $r.Dispose() }
}

$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "anthropic stub on http://localhost:$Port/ (scripts: $Dir)"
while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    try {
        $path = $ctx.Request.Url.AbsolutePath
        if ($ctx.Request.HttpMethod -eq 'POST' -and $path -eq '/__reset') {
            $body = Read-Body $ctx
            if ($body) { $o = $body | ConvertFrom-Json -AsHashtable; if ($o.dir) { $Dir = $o.dir } }
            $script:n = 0
            Remove-Item -Recurse -Force (Join-Path $Dir 'requests') -ErrorAction SilentlyContinue
            $ctx.Response.StatusCode = 200; $ctx.Response.Close()
            continue
        }
        if ($ctx.Request.HttpMethod -eq 'GET' -and $path -eq '/') { $ctx.Response.StatusCode = 200; $ctx.Response.Close(); continue }
        if ($ctx.Request.HttpMethod -ne 'POST' -or $path -ne '/v1/messages') { $ctx.Response.StatusCode = 404; $ctx.Response.Close(); continue }
        $script:n++
        $name = '{0:D3}' -f $script:n
        $reqDir = New-Item -ItemType Directory -Force (Join-Path $Dir 'requests')
        Set-Content (Join-Path $reqDir "$name.json") (Read-Body $ctx) -NoNewline
        Set-Content (Join-Path $reqDir "$name.key") "$($ctx.Request.Headers['x-api-key'])" -NoNewline
        Set-Content (Join-Path $reqDir "$name.headers") (@{ authorization = "$($ctx.Request.Headers['Authorization'])"
            'anthropic-beta' = "$($ctx.Request.Headers['anthropic-beta'])"; 'x-api-key' = "$($ctx.Request.Headers['x-api-key'])" } | ConvertTo-Json -Compress) -NoNewline
        $file = Join-Path $Dir "$name.json"
        $script = if (Test-Path $file) { Get-Content $file -Raw | ConvertFrom-Json -AsHashtable }
                  else { @{ status = 500; body = @{ type = 'error'; error = @{ type = 'api_error'; message = "stub has no script $name" } } } }
        Send-Script $ctx $script
    } catch {
        Write-Warning "stub: $_"
        try { $ctx.Response.StatusCode = 500; $ctx.Response.Close() } catch { }
    }
}
