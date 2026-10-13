<#
.SYNOPSIS
  The factory dashboard server: http://localhost:4317. See "Dashboard API" in docs/FACTORY-CONTRACT.md.
.DESCRIPTION
  Serves factory/dashboard/index.html, its vendor/ files, read-only files under the state dir
  (/state/...) and agent transcripts named by agent-start events. Every
  write is one factory.ps1 call; this script holds no transition logic. After an approve or rework
  it starts runner.ps1, and after a new idea it starts spec-run.ps1, both detached.
#>
param(
    [int] $Port = 4317,
    [string] $StateDir
)

$ErrorActionPreference = 'Stop'
$Scripts = $PSScriptRoot
$Factory = Join-Path $Scripts 'factory.ps1'
$Index = Join-Path (Split-Path $Scripts -Parent) 'dashboard/index.html'
$Vendor = Join-Path (Split-Path $Scripts -Parent) 'dashboard/vendor'

if (-not $StateDir) {
    $StateDir = if ($env:FACTORY_HOME) { $env:FACTORY_HOME } else {
        Join-Path (Split-Path ((git rev-parse --path-format=absolute --git-common-dir) -replace '/', '\') -Parent) '.factory-runs'
    }
}
$StateDir = [IO.Path]::GetFullPath($StateDir)
New-Item -ItemType Directory -Force $StateDir | Out-Null
$env:FACTORY_HOME = $StateDir

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
    $bytes = [IO.File]::ReadAllBytes($path)
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

    if ($method -eq 'GET' -and $path -eq '/api/target') {
        $p = $ctx.Request.QueryString['path']
        $abs = [bool]($p -and [IO.Path]::IsPathFullyQualified($p))
        $exists = $abs -and (Test-Path $p -PathType Container)
        $isRepo = $exists -and (Test-Path (Join-Path $p '.git'))
        $empty = $exists -and -not $isRepo -and @(Get-ChildItem $p -Force).Count -eq 0
        $ok = $abs -and (-not (Test-Path $p) -or $isRepo -or $empty)
        return Send $ctx 200 (@{ ok = $ok; absolute = $abs; exists = $exists; isRepo = $isRepo; empty = $empty } | ConvertTo-Json -Compress)
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

    if ($method -eq 'POST' -and $path -eq '/api/ideas') {
        $req = Read-Body $ctx | ConvertFrom-Json
        $text = $req.text
        if (-not $text -or -not $text.Trim()) { return Send $ctx 400 '{"ok":false,"message":"an idea needs some text"}' }
        $target = if ($req.PSObject.Properties['target'] -and $req.target) { $req.target.Trim() } else { '' }
        $r = Invoke-Factory new-idea $text.Trim() -Target $target
        if ($r.Ok) { Start-Detached 'spec-run.ps1' @($r.Message) }
        return Send-Result $ctx $r
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

    Send $ctx 404 '{"ok":false,"message":"not found"}'
}

$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "factory dashboard on http://localhost:$Port/  (state: $StateDir)"
try {
    while ($listener.IsListening) {
        $task = $listener.GetContextAsync()
        while (-not $task.AsyncWaitHandle.WaitOne(250)) { }
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
