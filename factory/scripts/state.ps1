<#
.SYNOPSIS
  The factory's state backends: local files (the default), or an Azure Blob container that
  FACTORY_HOME caches. See "State backends" in docs/FACTORY-CONTRACT.md.
.DESCRIPTION
  Dot-sourced by factory.ps1, serve.ps1, runner.ps1 and idea-turn.ps1; call Initialize-State first.
  Talks to Blob storage over REST with an Entra bearer token (no Az module, no shared keys).
  Everything is a no-op, or plain file IO, when FACTORY_STATE is local.
#>

. (Join-Path $PSScriptRoot 'env-file.ps1')

$script:StateLib = $PSCommandPath
$script:StateLeaseSeconds = 60
$script:StateBoardVersionsKept = 50
$script:StateHttp = [Net.Http.HttpClient]::new()
$script:StateHttp.Timeout = [TimeSpan]::FromSeconds(100)

# --- settings ----------------------------------------------------------------------------------

function Initialize-State([string] $StateHome, [string] $EnvFile) {
    $vars = if ($EnvFile) { Read-EnvFile $EnvFile } else { @{} }
    $setting = { param($n) $v = [Environment]::GetEnvironmentVariable($n); if ($v) { $v } else { $vars[$n] } }
    $mode = & $setting 'FACTORY_STATE'
    if (-not $mode) { $mode = 'local' }
    if ($mode -notin 'local', 'azure') { throw "state: FACTORY_STATE '$mode' must be local or azure" }
    $url = & $setting 'FACTORY_BLOB_URL'
    if ($mode -eq 'azure' -and -not $url) { throw 'state: FACTORY_STATE=azure needs FACTORY_BLOB_URL (the container URL)' }
    $cap = & $setting 'FACTORY_STATE_CAP'
    $script:StateHome = $StateHome
    $script:StateEnvFile = $EnvFile
    $script:StateMode = $mode
    # Tests: {home} becomes a hash of FACTORY_HOME, so every test's home gets its own container.
    if ($url -and $url.Contains('{home}')) {
        $h = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($StateHome.ToLowerInvariant()))).Substring(0, 16).ToLowerInvariant()
        $url = $url.Replace('{home}', "h$h")
    }
    $script:StateUrl = if ($url) { $url.TrimEnd('/') } else { $null }
    $script:StateCap = if ($cap) { [long]$cap } else { 1GB }
    $script:StateAccount = & $setting 'FACTORY_STORAGE_ACCOUNT'
    $script:StateGroup = & $setting 'FACTORY_STATE_RG'
    $script:StateToken = $null
    $script:StateManifest = $null
}

function State-IsAzure { $script:StateMode -eq 'azure' }

# Outside FACTORY_HOME, so nothing here is ever synced: the token cache and allow-ip's last IP.
function State-LocalDir {
    if ($env:FACTORY_LOCAL_DIR) { return $env:FACTORY_LOCAL_DIR }
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'claude-factory'
}

# --- auth --------------------------------------------------------------------------------------

function Get-StateToken {
    if ($env:FACTORY_STATE_TOKEN) { return $env:FACTORY_STATE_TOKEN }
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    if ($script:StateToken -and $script:StateToken.expiresOn -gt $now + 300) { return $script:StateToken.token }
    if ($env:IDENTITY_ENDPOINT -and $env:IDENTITY_HEADER) {
        # A managed identity (Container Apps, App Service): the deployed factory.
        $r = Invoke-RestMethod "$($env:IDENTITY_ENDPOINT)?resource=https://storage.azure.com&api-version=2019-08-01" -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }
        $script:StateToken = @{ token = $r.access_token; expiresOn = [long]$r.expires_on }
        return $script:StateToken.token
    }
    $cache = Join-Path (State-LocalDir) 'storage-token.json'
    if (Test-Path $cache) {
        try {
            $c = Get-Content $cache -Raw | ConvertFrom-Json
            if ([long]$c.expiresOn -gt $now + 300) { $script:StateToken = @{ token = $c.token; expiresOn = [long]$c.expiresOn }; return $c.token }
        } catch { }
    }
    $azArgs = 'account', 'get-access-token', '--resource', 'https://storage.azure.com', '--output', 'json'
    # Piped, if only '': a child that inherited this process's stdin would wait on it (see Invoke-Tool).
    $out = if ($env:FACTORY_AZ_CMD) { '' | & pwsh -NoProfile -File $env:FACTORY_AZ_CMD @azArgs 2>&1 } else { '' | & az @azArgs 2>&1 }
    $text = (@($out | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }) | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $text) { throw "state: az account get-access-token failed (is az logged in?): $(($out | Out-String).Trim())" }
    $j = $text | ConvertFrom-Json
    $exp = if ($j.PSObject.Properties['expires_on']) { [long]$j.expires_on } else { ([DateTimeOffset][datetime]$j.expiresOn).ToUnixTimeSeconds() }
    $script:StateToken = @{ token = $j.accessToken; expiresOn = $exp }
    New-Item -ItemType Directory -Force (State-LocalDir) | Out-Null
    @{ token = $j.accessToken; expiresOn = $exp } | ConvertTo-Json | Set-Content $cache
    $j.accessToken
}

# --- REST --------------------------------------------------------------------------------------

# One Blob service call; retried on a transport error or a 5xx. Returns { Status, Headers, Bytes }.
function Invoke-Blob([string] $Method, [string] $Name, [string] $Query = '', [hashtable] $Headers = @{}, [byte[]] $Body = $null) {
    $uri = $script:StateUrl
    if ($Name) { $uri += '/' + ((($Name -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/') }
    if ($Query) { $uri += "?$Query" }
    for ($try = 1; ; $try++) {
        $req = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $uri)
        $null = $req.Headers.TryAddWithoutValidation('Authorization', "Bearer $(Get-StateToken)")
        $null = $req.Headers.TryAddWithoutValidation('x-ms-version', '2023-11-03')
        $null = $req.Headers.TryAddWithoutValidation('x-ms-date', [DateTime]::UtcNow.ToString('R'))
        foreach ($k in $Headers.Keys) { $null = $req.Headers.TryAddWithoutValidation($k, [string]$Headers[$k]) }
        if ($null -ne $Body) { $req.Content = [Net.Http.ByteArrayContent]::new($Body) }
        elseif ($Method -eq 'PUT') { $req.Content = [Net.Http.ByteArrayContent]::new([byte[]]@()) }
        try { $res = $script:StateHttp.SendAsync($req).GetAwaiter().GetResult() }
        catch {
            if ($try -lt 3) { Start-Sleep -Milliseconds (250 * $try); continue }
            throw "state: $Method $Name failed: $($_.Exception.Message)"
        }
        $status = [int]$res.StatusCode
        if ($status -ge 500 -and $try -lt 3) { $res.Dispose(); Start-Sleep -Milliseconds (250 * $try); continue }
        $h = [hashtable]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($kv in $res.Headers) { $h[$kv.Key] = $kv.Value -join ',' }
        foreach ($kv in $res.Content.Headers) { $h[$kv.Key] = $kv.Value -join ',' }
        $bytes = $res.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $res.Dispose()
        return [pscustomobject]@{ Status = $status; Headers = $h; Bytes = $bytes }
    }
}

function Blob-Error($r, [string] $what) { "state: $what answered $($r.Status) $($r.Headers['x-ms-error-code'])" }

# ETags compare without the quotes the list XML leaves off and the headers put on.
function Bare-ETag([string] $e) { if ($e) { $e.Trim('"') } else { $null } }
function Quote-ETag([string] $e) { if ($e) { '"' + (Bare-ETag $e) + '"' } else { $null } }

# $null when there is no such blob; { NotModified } when -ETag is still current; else { Bytes, ETag }.
function Read-StateBlob([string] $Name, [string] $ETag) {
    $h = @{}
    if ($ETag) { $h['If-None-Match'] = Quote-ETag $ETag }
    $r = Invoke-Blob GET $Name -Headers $h
    if ($r.Status -eq 404) { return $null }
    if ($r.Status -eq 304) { return [pscustomobject]@{ NotModified = $true; ETag = (Bare-ETag $ETag); Bytes = $null } }
    if ($r.Status -ne 200) { throw (Blob-Error $r "GET $Name") }
    [pscustomobject]@{ NotModified = $false; ETag = (Bare-ETag $r.Headers['ETag']); Bytes = $r.Bytes }
}

# Writes a block blob; returns its new ETag. -IfMatch / -IfNoneMatchAny make it conditional, and a
# failed condition is a 'state conflict'.
function Write-StateBlob([string] $Name, [byte[]] $Bytes, [string] $IfMatch, [switch] $IfNoneMatchAny) {
    $h = @{ 'x-ms-blob-type' = 'BlockBlob' }
    if ($IfMatch) { $h['If-Match'] = Quote-ETag $IfMatch }
    if ($IfNoneMatchAny) { $h['If-None-Match'] = '*' }
    $r = Invoke-Blob PUT $Name -Headers $h -Body $Bytes
    if ($r.Status -eq 412 -or ($r.Status -eq 409 -and $IfNoneMatchAny)) { throw "state conflict: $Name changed in Azure since it was read" }
    if ($r.Status -ne 201) { throw (Blob-Error $r "PUT $Name") }
    Bare-ETag $r.Headers['ETag']
}

# Appends one block to an append blob, creating it first if need be. Returns { ETag, Offset }.
function Add-StateBlock([string] $Name, [byte[]] $Bytes) {
    for ($try = 0; $try -lt 2; $try++) {
        $r = Invoke-Blob PUT $Name 'comp=appendblock' -Body $Bytes
        if ($r.Status -eq 201) { return [pscustomobject]@{ ETag = (Bare-ETag $r.Headers['ETag']); Offset = [long]$r.Headers['x-ms-blob-append-offset'] } }
        if ($r.Status -ne 404) { break }
        $c = Invoke-Blob PUT $Name -Headers @{ 'x-ms-blob-type' = 'AppendBlob'; 'If-None-Match' = '*' }
        if ($c.Status -notin 201, 409) { throw (Blob-Error $c "create $Name") }
    }
    throw (Blob-Error $r "append to $Name")
}

function Remove-StateBlob([string] $Name, [string] $VersionId) {
    $q = if ($VersionId) { "versionid=$([Uri]::EscapeDataString($VersionId))" } else { '' }
    $r = Invoke-Blob DELETE $Name $q
    if ($r.Status -notin 202, 404) { throw (Blob-Error $r "DELETE $Name") }
}

# Every blob under -Prefix (and, with -Versions, every old version):
# { Name, VersionId, IsCurrent, Length, LastModified, ETag, Leased }.
function Get-StateList([string] $Prefix = '', [switch] $Versions) {
    $marker = ''
    do {
        $q = "restype=container&comp=list&prefix=$([Uri]::EscapeDataString($Prefix))"
        if ($Versions) { $q += '&include=versions' }
        if ($marker) { $q += "&marker=$([Uri]::EscapeDataString($marker))" }
        $r = Invoke-Blob GET '' $q
        if ($r.Status -ne 200) { throw (Blob-Error $r 'list') }
        $xml = [xml]([Text.Encoding]::UTF8.GetString($r.Bytes).TrimStart([char]0xFEFF))
        # An empty <Blobs/> parses as a string, not an element, so .Blob would throw under strict mode.
        foreach ($b in @($xml.SelectNodes('/EnumerationResults/Blobs/Blob'))) {
            if (-not $b) { continue }
            $p = $b.Properties
            $vid = if ($b['VersionId']) { $b.VersionId } else { $null }
            [pscustomobject]@{
                Name = $b.Name; VersionId = $vid
                IsCurrent = (-not $Versions) -or ($b['IsCurrentVersion'] -and $b.IsCurrentVersion -eq 'true')
                Length = [long]$p.'Content-Length'; LastModified = [datetime]::Parse($p.'Last-Modified').ToUniversalTime()
                ETag = (Bare-ETag $p.Etag); Leased = ($p['LeaseState'] -and $p.LeaseState -eq 'leased')
            }
        }
        $marker = "$($xml.EnumerationResults.NextMarker)"
    } while ($marker)
}

# --- leases ------------------------------------------------------------------------------------

# Takes the 60 s lease on locks/<Name>, waiting up to -TimeoutSeconds; the lease id, or $null.
function Enter-StateLease([string] $Name, [int] $TimeoutSeconds = 30) {
    $blob = "locks/$Name"
    $id = [guid]::NewGuid().ToString()
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $r = Invoke-Blob PUT $blob 'comp=lease' @{ 'x-ms-lease-action' = 'acquire'; 'x-ms-lease-duration' = "$script:StateLeaseSeconds"; 'x-ms-proposed-lease-id' = $id }
        if ($r.Status -eq 201) { return $id }
        if ($r.Status -eq 404) {
            $c = Invoke-Blob PUT $blob -Headers @{ 'x-ms-blob-type' = 'BlockBlob'; 'If-None-Match' = '*' }
            if ($c.Status -notin 201, 409) { throw (Blob-Error $c "create $blob") }
            continue
        }
        if ($r.Status -ne 409) { throw (Blob-Error $r "lease $blob") }
        if ((Get-Date) -ge $deadline) { return $null }
        Start-Sleep -Milliseconds 500
    }
}

# $true when the lease was still ours (a lapsed one nobody took counts), $false when it is gone.
function Update-StateLease([string] $Name, [string] $Id) {
    $r = Invoke-Blob PUT "locks/$Name" 'comp=lease' @{ 'x-ms-lease-action' = 'renew'; 'x-ms-lease-id' = $Id }
    if ($r.Status -eq 200) { return $true }
    if ($r.Status -eq 409) { return $false }
    throw (Blob-Error $r "renew locks/$Name")
}

function Exit-StateLease([string] $Name, [string] $Id) {
    if (-not $Id) { return }
    $r = Invoke-Blob PUT "locks/$Name" 'comp=lease' @{ 'x-ms-lease-action' = 'release'; 'x-ms-lease-id' = $Id }
    if ($r.Status -notin 200, 409) { throw (Blob-Error $r "release locks/$Name") }
}

# Renews the lease every FACTORY_STATE_RENEW_SECONDS (default 20) on a background thread while the
# holder works; it stops by itself once the lease is lost.
function Start-StateLeaseKeeper([string] $Name, [string] $Id) {
    $every = if ($env:FACTORY_STATE_RENEW_SECONDS) { [int]$env:FACTORY_STATE_RENEW_SECONDS } else { 20 }
    Start-ThreadJob -ArgumentList $script:StateLib, $script:StateHome, $script:StateEnvFile, $Name, $Id, $every -ScriptBlock {
        param($lib, $stateHome, $envFile, $name, $id, $every)
        . $lib
        Initialize-State -StateHome $stateHome -EnvFile $envFile
        while ($true) {
            Start-Sleep -Seconds $every
            try { if (-not (Update-StateLease $name $id)) { break } } catch { }
        }
    }
}

function Stop-StateLeaseKeeper($Job) {
    if (-not $Job) { return }
    Stop-Job $Job -ErrorAction SilentlyContinue
    Remove-Job $Job -Force -ErrorAction SilentlyContinue
}

# --- the cache's manifest ----------------------------------------------------------------------

# .sync.json: path -> { len, mtime (UTC ticks), etag, pruned }, what this cache last synced.
function Get-Manifest {
    if ($null -eq $script:StateManifest) {
        $p = Join-Path $script:StateHome '.sync.json'
        $script:StateManifest = if (Test-Path $p) { Get-Content $p -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
        if ($null -eq $script:StateManifest) { $script:StateManifest = @{} }
    }
    $script:StateManifest
}

function Save-Manifest { Write-StateLocal (Join-Path $script:StateHome '.sync.json') ([Text.Encoding]::UTF8.GetBytes((Get-Manifest | ConvertTo-Json -Depth 5))) }

# Atomic: a temp file, then a rename, retried while a reader holds the target.
function Write-StateLocal([string] $Path, [byte[]] $Bytes) {
    New-Item -ItemType Directory -Force (Split-Path $Path -Parent) | Out-Null
    $tmp = "$Path.$PID.tmp"
    [IO.File]::WriteAllBytes($tmp, $Bytes)
    for ($i = 0; ; $i++) {
        try { [IO.File]::Move($tmp, $Path, $true); return }
        catch [UnauthorizedAccessException], [IO.IOException] { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 50 }
    }
}

function Read-StateLocal([string] $Path) {
    $fs = [IO.File]::Open($Path, 'Open', 'Read', [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    # The comma keeps the byte[] whole: unrolled, it is slow, and a 1-byte file comes back a scalar.
    try { $ms = [IO.MemoryStream]::new(); $fs.CopyTo($ms); , $ms.ToArray() } finally { $fs.Dispose() }
}

function Stamp([string] $Path, [string] $ETag) {
    $f = Get-Item -LiteralPath $Path
    @{ len = $f.Length; mtime = $f.LastWriteTimeUtc.Ticks; etag = $ETag }
}

# Whether a relative path is synced by Push-State and Pull-State (board and events have their own).
function Test-StateSynced([string] $Rel) {
    if ($Rel -in 'board.json', 'events.jsonl', 'headless-settings.json', '.sync.json') { return $false }
    if ($Rel -like '*.tmp' -or $Rel -like '*.lock' -or $Rel -like 'events/*' -or $Rel -like 'locks/*' -or $Rel -like '.local/*') { return $false }
    $true
}

function Rel-Path([string] $Full) { [IO.Path]::GetRelativePath($script:StateHome, $Full) -replace '\\', '/' }

# --- sync --------------------------------------------------------------------------------------

# Uploads every synced file changed since the manifest, except where -Skip { param($rel) } says so,
# and deletes the blob of every manifest path gone locally. Returns how many files it uploaded.
function Push-State([scriptblock] $Skip) {
    if (-not (State-IsAzure)) { return 0 }
    $m = Get-Manifest
    $seen = [Collections.Generic.HashSet[string]]::new()
    $n = 0
    foreach ($f in Get-ChildItem -LiteralPath $script:StateHome -Recurse -File -Force -ErrorAction SilentlyContinue) {
        $rel = Rel-Path $f.FullName
        if (-not (Test-StateSynced $rel)) { continue }
        $null = $seen.Add($rel)
        $e = $m[$rel]
        if ($e -and $e['pruned']) { continue }
        $ticks = $f.LastWriteTimeUtc.Ticks
        if ($e -and $e['len'] -eq $f.Length -and $e['mtime'] -eq $ticks) { continue }
        if ($Skip -and (& $Skip $rel)) { continue }
        $bytes = Read-StateLocal $f.FullName
        $etag = Write-StateBlob $rel $bytes
        $m[$rel] = @{ len = $bytes.Length; mtime = $ticks; etag = $etag }
        $n++
    }
    foreach ($rel in @($m.Keys)) {
        if ($seen.Contains($rel) -or -not (Test-StateSynced $rel)) { continue }
        if (-not $m[$rel]['pruned']) { Remove-StateBlob $rel }
        $m.Remove($rel)
    }
    Save-Manifest
    $n
}

# Downloads every synced blob under -Prefix whose ETag differs from the manifest, except where the
# local file changed since its last sync (or was never synced): local changes win. Never deletes.
function Pull-State([string] $Prefix = '') {
    if (-not (State-IsAzure)) { return 0 }
    $m = Get-Manifest
    $n = 0
    foreach ($b in Get-StateList -Prefix $Prefix) {
        $rel = $b.Name
        if (-not (Test-StateSynced $rel)) { continue }
        $e = $m[$rel]
        if ($e -and $e['etag'] -eq $b.ETag) { continue }
        $p = Join-Path $script:StateHome $rel
        if (Test-Path -LiteralPath $p) {
            if (-not $e) { continue }
            $f = Get-Item -LiteralPath $p
            if ($f.Length -ne $e['len'] -or $f.LastWriteTimeUtc.Ticks -ne $e['mtime']) { continue }
        }
        $r = Read-StateBlob $rel
        if (-not $r) { continue }
        Write-StateLocal $p $r.Bytes
        $m[$rel] = Stamp $p $r.ETag
        $n++
    }
    Save-Manifest
    $n
}

# Refreshes the local board.json from the container: 'changed', 'same', or 'none' (no board anywhere).
function Sync-Board {
    $m = Get-Manifest
    $e = $m['board.json']
    $local = Join-Path $script:StateHome 'board.json'
    $r = Read-StateBlob 'board.json' -ETag $(if ($e) { $e['etag'] } else { $null })
    if ($null -eq $r) {
        if ($e) { throw 'state: board.json is gone from the container; restore a version of it, or point FACTORY_BLOB_URL at the right container' }
        if (Test-Path $local) { throw "state: the container has no board.json but $local exists; run factory.ps1 state-migrate" }
        return 'none'
    }
    if ($r.NotModified) { return 'same' }
    Write-StateLocal $local $r.Bytes
    $m['board.json'] = Stamp $local $r.ETag
    Save-Manifest
    'changed'
}

# Writes board.json to the container (If-Match the ETag it was read at), then locally.
function Save-StateBoard([string] $Json) {
    $local = Join-Path $script:StateHome 'board.json'
    $bytes = [Text.Encoding]::UTF8.GetBytes($Json)
    if (-not (State-IsAzure)) { Write-StateLocal $local $bytes; return }
    $m = Get-Manifest
    $e = $m['board.json']
    $etag = if ($e) { Write-StateBlob 'board.json' $bytes -IfMatch $e['etag'] } else { Write-StateBlob 'board.json' $bytes -IfNoneMatchAny }
    Write-StateLocal $local $bytes
    $m['board.json'] = Stamp $local $etag
    Save-Manifest
}

function Event-Month { "events/$((Get-Date).ToUniversalTime().ToString('yyyy-MM')).jsonl" }

# Appends one event line to events.jsonl, and in azure to this month's append blob.
function Add-StateEvent([string] $Line) {
    [IO.File]::AppendAllText((Join-Path $script:StateHome 'events.jsonl'), "$Line`n")
    if (-not (State-IsAzure)) { return }
    $bytes = [Text.Encoding]::UTF8.GetBytes("$Line`n")
    $name = Event-Month
    $m = Get-Manifest
    $e = $m[$name]
    $r = Add-StateBlock $name $bytes
    # Another machine's block landed in between: leave the ETag unknown so Sync-Events rebuilds.
    $m[$name] = if ($r.Offset -eq $(if ($e) { [long]$e['len'] } else { 0 })) { @{ etag = $r.ETag; len = $r.Offset + $bytes.Length } } else { @{ etag = ''; len = 0 } }
    $m['events/'] = @{ etag = 'listed' }
    Save-Manifest
}

# Rebuilds events.jsonl from the month blobs when this month's changed (a conditional GET, cheap on
# every poll), or on a cache that has never listed them. $true when it rewrote the file.
function Sync-Events {
    if (-not (State-IsAzure)) { return $false }
    $m = Get-Manifest
    $cur = Event-Month
    $e = $m[$cur]
    $r = Read-StateBlob $cur -ETag $(if ($e -and $e['etag']) { $e['etag'] } else { $null })
    if ($m['events/'] -and ($null -eq $r -or $r.NotModified)) { return $false }
    $months = @(Get-StateList -Prefix 'events/' | Sort-Object Name)
    $m['events/'] = @{ etag = 'listed' }
    if (-not $months) { Save-Manifest; return $false }
    $all = [IO.MemoryStream]::new()
    foreach ($b in $months) {
        $got = Read-StateBlob $b.Name
        if (-not $got) { continue }
        $all.Write($got.Bytes, 0, $got.Bytes.Length)
        $m[$b.Name] = @{ etag = $got.ETag; len = $got.Bytes.Length }
    }
    Write-StateLocal (Join-Path $script:StateHome 'events.jsonl') $all.ToArray()
    Save-Manifest
    $true
}

# The one-time upload of an existing FACTORY_HOME: events by month, then the files, then the board
# (last, so an interrupted migrate can run again). Returns how many blobs it wrote.
function Invoke-StateMigrate {
    if (Read-StateBlob 'board.json') { throw 'state-migrate refused: the container already has a board.json' }
    $m = Get-Manifest
    $n = 0
    $events = Join-Path $script:StateHome 'events.jsonl'
    if (Test-Path $events) {
        $byMonth = [ordered]@{}
        foreach ($line in [IO.File]::ReadAllLines($events)) {
            if (-not $line.Trim()) { continue }
            $month = if ($line -match '"ts"\s*:\s*"(\d{4}-\d{2})') { $Matches[1] } else { (Get-Date).ToUniversalTime().ToString('yyyy-MM') }
            if (-not $byMonth.Contains($month)) { $byMonth[$month] = [Text.StringBuilder]::new() }
            $null = $byMonth[$month].Append("$line`n")
        }
        foreach ($month in $byMonth.Keys) {
            $name = "events/$month.jsonl"
            Remove-StateBlob $name   # a migrate that stopped before the board left this behind
            $bytes = [Text.Encoding]::UTF8.GetBytes($byMonth[$month].ToString())
            $last = $null
            # An append block is at most 4 MiB.
            for ($at = 0; $at -lt $bytes.Length; $at += 4MB) {
                $chunk = [byte[]]::new([Math]::Min(4MB, $bytes.Length - $at))
                [Array]::Copy($bytes, $at, $chunk, 0, $chunk.Length)
                $last = Add-StateBlock $name $chunk
            }
            $m[$name] = @{ etag = $last.ETag; len = $bytes.Length }
            $n++
        }
        $m['events/'] = @{ etag = 'listed' }
    }
    $n += Push-State
    $board = Join-Path $script:StateHome 'board.json'
    if (Test-Path $board) { Save-StateBoard ([IO.File]::ReadAllText($board)); $n++ }
    $n
}

# --- the cap -----------------------------------------------------------------------------------

# Rolls the container back under FACTORY_STATE_CAP, in the contract's order. -Jobs is the board's
# jobs (id, stage, updatedAt). Returns { Used, Cap, Deleted, Freed }.
function Invoke-StatePrune($Jobs) {
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($b in Get-StateList -Versions) { $rows.Add($b) }
    $used = [long]0; foreach ($r in $rows) { $used += $r.Length }
    $result = [pscustomobject]@{ Used = $used; Cap = $script:StateCap; Deleted = 0; Freed = [long]0 }
    if ($used -le $script:StateCap) { return $result }

    $order = [Collections.Generic.List[object]]::new()
    # 1. Old versions: every one but board.json's newest 50.
    $boardOld = @($rows | Where-Object { $_.Name -eq 'board.json' -and -not $_.IsCurrent } | Sort-Object VersionId -Descending | Select-Object -Skip $script:StateBoardVersionsKept)
    foreach ($r in @($rows | Where-Object { -not $_.IsCurrent -and $_.Name -ne 'board.json' } | Sort-Object LastModified) + $boardOld) { $order.Add($r) }
    # 2-3. Finished jobs (done, discarded or purged), oldest first: their bulky logs, then the rest.
    $byId = @{}; foreach ($j in @($Jobs)) { $byId["$($j.id)"] = $j }
    $jobRows = @($rows | Where-Object { $_.Name -match '^jobs/([^/]+)/' })
    $finished = foreach ($g in $jobRows | Group-Object { ($_.Name -split '/')[1] }) {
        $j = $byId[$g.Name]
        if ($j -and $j.stage -notin 'done', 'discarded') { continue }
        $at = if ($j) { [datetime]::Parse($j.updatedAt, [Globalization.CultureInfo]::InvariantCulture, 'AdjustToUniversal') } else { ($g.Group | Measure-Object LastModified -Maximum).Maximum }
        [pscustomobject]@{ Id = $g.Name; At = $at; Rows = $g.Group }
    }
    $finished = @($finished | Sort-Object At)
    $bulky = '^jobs/[^/]+/(run-[^/]*\.jsonl|transcripts/.*|deploy-[^/]*\.log|gate\.log)$'
    foreach ($f in $finished) { foreach ($r in $f.Rows | Where-Object { $_.Name -match $bulky }) { $order.Add($r) } }
    foreach ($f in $finished) { foreach ($r in $f.Rows | Where-Object { $_.Name -notmatch $bulky }) { $order.Add($r) } }
    # 4. Sessions whose turn lease is free, oldest session.json first.
    $leased = @($rows | Where-Object { $_.IsCurrent -and $_.Leased -and $_.Name -like 'locks/turn-*' } | ForEach-Object { $_.Name.Substring(11) })
    $sessions = foreach ($g in @($rows | Where-Object { $_.Name -match '^sessions/([^/]+)/' }) | Group-Object { ($_.Name -split '/')[1] }) {
        if ($g.Name -in $leased) { continue }
        $s = $g.Group | Where-Object { $_.Name -like '*/session.json' -and $_.IsCurrent } | Select-Object -First 1
        [pscustomobject]@{ At = $(if ($s) { $s.LastModified } else { [datetime]::MinValue }); Rows = $g.Group }
    }
    foreach ($s in @($sessions | Sort-Object At)) { foreach ($r in $s.Rows) { $order.Add($r) } }

    $m = Get-Manifest
    $done = [Collections.Generic.HashSet[object]]::new()
    foreach ($r in $order) {
        if ($used -le $script:StateCap) { break }
        if (-not $done.Add($r)) { continue }
        if ($r.IsCurrent) {
            Remove-StateBlob $r.Name
            # With versioning on, the deleted blob lives on as a version until that is deleted too.
            if ($r.VersionId) { Remove-StateBlob $r.Name $r.VersionId }
            $e = $m[$r.Name]
            if ($e) { $e['pruned'] = $true } else { $m[$r.Name] = @{ pruned = $true } }
        } else {
            Remove-StateBlob $r.Name $r.VersionId
        }
        $used -= $r.Length
        $result.Freed += $r.Length
        $result.Deleted++
    }
    Save-Manifest
    $result.Used = $used
    $result
}

# --- the firewall ------------------------------------------------------------------------------

# Adds this machine's public IP to the storage account's firewall, and removes the one added last.
function Invoke-StateAllowIp {
    $account = if ($script:StateAccount) { $script:StateAccount } else { ([Uri]$script:StateUrl).Host.Split('.')[0] }
    $group = if ($script:StateGroup) { $script:StateGroup } else { 'rg-factory-state' }
    $ipUrl = if ($env:FACTORY_IP_URL) { $env:FACTORY_IP_URL } else { 'https://api.ipify.org' }
    $ip = "$(Invoke-RestMethod $ipUrl -TimeoutSec 15)".Trim()
    if ($ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw "state: $ipUrl answered '$ip', not an IPv4 address" }
    $record = Join-Path (State-LocalDir) 'allowed-ip.txt'
    $old = if (Test-Path $record) { "$(Get-Content $record -Raw)".Trim() } else { $null }
    $az = {
        param([string[]] $a)
        $out = if ($env:FACTORY_AZ_CMD) { '' | & pwsh -NoProfile -File $env:FACTORY_AZ_CMD @a 2>&1 } else { '' | & az @a 2>&1 }
        if ($LASTEXITCODE -ne 0) { throw "state: az $($a[0..3] -join ' ') failed: $(($out | Out-String).Trim())" }
    }
    if ($old -ne $ip) {
        & $az @('storage', 'account', 'network-rule', 'add', '--account-name', $account, '--resource-group', $group, '--ip-address', $ip, '--output', 'none')
        if ($old) { try { & $az @('storage', 'account', 'network-rule', 'remove', '--account-name', $account, '--resource-group', $group, '--ip-address', $old, '--output', 'none') } catch { } }
        New-Item -ItemType Directory -Force (State-LocalDir) | Out-Null
        Set-Content $record $ip
    }
    [pscustomobject]@{ Ip = $ip; Removed = $(if ($old -and $old -ne $ip) { $old } else { $null }); Account = $account }
}
