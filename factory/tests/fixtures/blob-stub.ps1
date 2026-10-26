<#
.SYNOPSIS
  A stand-in for the slice of the Azure Blob REST API that state.ps1 uses, so the azure state backend
  can be tested without an account or a network.
.DESCRIPTION
  Blobs are kept in memory, keyed <container>/<blob>, with versioning on (as the real account has it): an overwrite or a delete
  keeps the old content as a version, and a version is deleted with ?versionid=. Every request
  needs `Authorization: Bearer <Token>`; anything else is 403 (the real account has shared keys off).
  Served, under /<container>/<blob>:
    PUT (x-ms-blob-type BlockBlob | AppendBlob; If-Match, If-None-Match: *) · GET (If-None-Match) ·
    DELETE [?versionid=] · PUT ?comp=appendblock · PUT ?comp=lease (acquire, renew, release, break)
    GET ?restype=container&comp=list[&prefix=][&include=versions][&marker=] (pages of -PageSize)
  Leases run on the clock (-LeaseScale shortens them for tests). A write to a leased blob without its
  lease id is 412. GET /_stub/state answers the blobs as JSON, PUT /_stub/token changes the token
  it accepts, and GET|PUT /_stub/ip is allow-ip's public-IP lookup, so tests can assert on them. Every request is appended to <Log> as { method, path, query }.
#>
param(
    [Parameter(Mandatory)] [int] $Port,
    [string] $Token = 'test-token',
    [string] $Log,
    [int] $PageSize = 1000,
    [double] $LeaseScale = 1.0
)

$ErrorActionPreference = 'Stop'

# name -> @{ type; bytes; etag; modified; versionId; lease = @{ id; until } | $null }
$blobs = @{}
# @{ name; versionId; bytes; type; modified } for every non-current version
$versions = [Collections.Generic.List[object]]::new()
$clock = 0

function New-VersionId {
    $script:clock++
    (Get-Date).ToUniversalTime().AddTicks($script:clock).ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
}
function New-ETag { '"0x' + [guid]::NewGuid().ToString('N').Substring(0, 16).ToUpperInvariant() + '"' }

function Lease-Active($b) { $b -and $b.lease -and ($b.lease.until -eq [datetime]::MaxValue -or $b.lease.until -gt (Get-Date)) }

# 412 when the blob is leased and the request does not carry that lease, else $null.
function Lease-Refusal($b, $req) {
    if (-not (Lease-Active $b)) { return $null }
    if ($req.Headers['x-ms-lease-id'] -eq $b.lease.id) { return $null }
    @{ status = 412; code = 'LeaseIdMissing' }
}

function Keep-Version([string] $name, $b) {
    $versions.Add(@{ name = $name; versionId = $b.versionId; bytes = $b.bytes; type = $b.type; modified = $b.modified })
}

function Put-Blob([string] $name, $req, [byte[]] $body) {
    $b = $blobs[$name]
    $ifMatch = $req.Headers['If-Match']; $ifNone = $req.Headers['If-None-Match']
    if ($ifNone -eq '*' -and $b) { return @{ status = 409; code = 'BlobAlreadyExists' } }
    if ($ifMatch -and (-not $b -or $b.etag -ne $ifMatch)) { return @{ status = 412; code = 'ConditionNotMet' } }
    $r = Lease-Refusal $b $req; if ($r) { return $r }
    if ($b) { Keep-Version $name $b }
    $type = $req.Headers['x-ms-blob-type']
    if ($type -notin 'BlockBlob', 'AppendBlob') { return @{ status = 400; code = 'MissingRequiredHeader' } }
    $lease = if ($b) { $b.lease } else { $null }
    # Assigned, not the output of an if: that unrolls an empty body to $null.
    $bytes = $body
    if ($type -eq 'AppendBlob' -or $null -eq $bytes) { $bytes = [byte[]]::new(0) }
    $blobs[$name] = @{ type = $type; bytes = $bytes; etag = (New-ETag)
        modified = (Get-Date).ToUniversalTime(); versionId = (New-VersionId); lease = $lease }
    @{ status = 201; headers = @{ ETag = $blobs[$name].etag; 'x-ms-version-id' = $blobs[$name].versionId } }
}

function Append-Block([string] $name, $req, [byte[]] $body) {
    $b = $blobs[$name]
    if (-not $b) { return @{ status = 404; code = 'BlobNotFound' } }
    if ($b.type -ne 'AppendBlob') { return @{ status = 409; code = 'InvalidBlobType' } }
    $r = Lease-Refusal $b $req; if ($r) { return $r }
    $offset = $b.bytes.Length
    $b.bytes = [byte[]]($b.bytes + $body)
    $b.etag = New-ETag; $b.modified = (Get-Date).ToUniversalTime()
    @{ status = 201; headers = @{ ETag = $b.etag; 'x-ms-blob-append-offset' = "$offset" } }
}

function Get-Blob([string] $name, $req) {
    $b = $blobs[$name]
    if (-not $b) { return @{ status = 404; code = 'BlobNotFound' } }
    if ($req.Headers['If-None-Match'] -eq $b.etag) { return @{ status = 304; headers = @{ ETag = $b.etag } } }
    @{ status = 200; bytes = $b.bytes; headers = @{ ETag = $b.etag; 'x-ms-blob-type' = $b.type } }
}

function Delete-Blob([string] $name, $req, [string] $versionId) {
    if ($versionId) {
        if ($blobs[$name] -and $blobs[$name].versionId -eq $versionId) { return @{ status = 403; code = 'OperationNotAllowedOnRootBlob' } }
        $v = $versions | Where-Object { $_.name -eq $name -and $_.versionId -eq $versionId } | Select-Object -First 1
        if (-not $v) { return @{ status = 404; code = 'BlobNotFound' } }
        $null = $versions.Remove($v)
        return @{ status = 202 }
    }
    $b = $blobs[$name]
    if (-not $b) { return @{ status = 404; code = 'BlobNotFound' } }
    $r = Lease-Refusal $b $req; if ($r) { return $r }
    Keep-Version $name $b
    $blobs.Remove($name)
    @{ status = 202 }
}

function Lease-Blob([string] $name, $req) {
    $b = $blobs[$name]
    if (-not $b) { return @{ status = 404; code = 'BlobNotFound' } }
    $action = $req.Headers['x-ms-lease-action']
    $id = $req.Headers['x-ms-lease-id']
    $active = Lease-Active $b
    switch ($action) {
        'acquire' {
            $dur = [int]$req.Headers['x-ms-lease-duration']
            $want = $req.Headers['x-ms-proposed-lease-id']; if (-not $want) { $want = [guid]::NewGuid().ToString() }
            if ($active -and $b.lease.id -ne $want) { return @{ status = 409; code = 'LeaseAlreadyPresent' } }
            $until = if ($dur -eq -1) { [datetime]::MaxValue } else { (Get-Date).AddSeconds($dur * $LeaseScale) }
            $b.lease = @{ id = $want; until = $until; duration = $dur }
            return @{ status = 201; headers = @{ 'x-ms-lease-id' = $want } }
        }
        'renew' {
            # A lapsed lease can be renewed while nobody else has taken it, as on the real service.
            if (-not $b.lease -or $b.lease.id -ne $id) { return @{ status = 409; code = 'LeaseIdMismatchWithLeaseOperation' } }
            $dur = $b.lease.duration
            $b.lease.until = if ($dur -eq -1) { [datetime]::MaxValue } else { (Get-Date).AddSeconds($dur * $LeaseScale) }
            return @{ status = 200; headers = @{ 'x-ms-lease-id' = $id } }
        }
        'release' {
            if (-not $b.lease -or $b.lease.id -ne $id) { return @{ status = 409; code = 'LeaseIdMismatchWithLeaseOperation' } }
            $b.lease = $null
            return @{ status = 200 }
        }
        'break' { $b.lease = $null; return @{ status = 202 } }
    }
    @{ status = 400; code = 'InvalidHeaderValue' }
}

function Esc([string] $s) { [Security.SecurityElement]::Escape($s) }

function Blob-Xml([string] $name, [string] $versionId, [bool] $current, [byte[]] $bytes, [datetime] $modified, [string] $etag, [string] $type, $b) {
    $x = "<Blob><Name>$(Esc $name)</Name>"
    if ($versionId) { $x += "<VersionId>$versionId</VersionId>" }
    if ($current) { $x += '<IsCurrentVersion>true</IsCurrentVersion>' }
    $lease = if ($current -and (Lease-Active $b)) { 'leased' } else { 'available' }
    $x += "<Properties><Last-Modified>$($modified.ToString('R'))</Last-Modified><Etag>$etag</Etag>"
    $x += "<Content-Length>$($bytes.Length)</Content-Length><BlobType>$type</BlobType><LeaseState>$lease</LeaseState></Properties></Blob>"
    $x
}

function List-Blobs([string] $container, $q) {
    $prefix = "$container/$($q['prefix'])"
    $withVersions = "$($q['include'])" -match 'versions'
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($n in $blobs.Keys) {
        if ($n.StartsWith($prefix)) { $b = $blobs[$n]; $rows.Add(@{ name = $n; versionId = $b.versionId; current = $true; b = $b; bytes = $b.bytes; modified = $b.modified; etag = $b.etag; type = $b.type }) }
    }
    if ($withVersions) {
        foreach ($v in $versions) {
            if ($v.name.StartsWith($prefix)) { $rows.Add(@{ name = $v.name; versionId = $v.versionId; current = $false; b = $null; bytes = $v.bytes; modified = $v.modified; etag = '"0xV"'; type = $v.type }) }
        }
    }
    $sorted = @($rows | Sort-Object { $_.name }, { $_.versionId })
    $start = if ($q['marker']) { [int]$q['marker'] } else { 0 }
    $page = @($sorted | Select-Object -Skip $start -First $PageSize)
    $next = if ($start + $PageSize -lt $sorted.Count) { "$($start + $PageSize)" } else { '' }
    $xml = '<?xml version="1.0" encoding="utf-8"?><EnumerationResults><Blobs>'
    foreach ($r in $page) {
        $vid = if ($withVersions) { $r.versionId } else { $null }
        $xml += Blob-Xml $r.name.Substring($container.Length + 1) $vid $r.current $r.bytes $r.modified $r.etag $r.type $r.b
    }
    $xml += "</Blobs><NextMarker>$next</NextMarker></EnumerationResults>"
    @{ status = 200; text = $xml; contentType = 'application/xml' }
}

function Handle($req, [byte[]] $body) {
    $q = [Web.HttpUtility]::ParseQueryString($req.Url.Query)
    $path = [Uri]::UnescapeDataString($req.Url.AbsolutePath.TrimStart('/'))
    if ($path -eq '_stub/state') {
        $out = @{}
        foreach ($n in $blobs.Keys) { $out[$n] = @{ type = $blobs[$n].type; text = [Text.Encoding]::UTF8.GetString($blobs[$n].bytes); leased = [bool](Lease-Active $blobs[$n]) } }
        return @{ status = 200; text = (@{ blobs = $out; versions = @($versions | ForEach-Object { @{ name = $_.name; versionId = $_.versionId } }) } | ConvertTo-Json -Depth 5); contentType = 'application/json' }
    }
    if ($path -eq '_stub/token' -and $req.HttpMethod -eq 'PUT') { $script:Token = [Text.Encoding]::UTF8.GetString($body); return @{ status = 200 } }
    # What allow-ip's "what is my IP" call answers (FACTORY_IP_URL); PUT changes it.
    if ($path -eq '_stub/ip') {
        if ($req.HttpMethod -eq 'PUT') { $script:Ip = [Text.Encoding]::UTF8.GetString($body) }
        return @{ status = 200; text = $(if ($script:Ip) { $script:Ip } else { '203.0.113.7' }); contentType = 'text/plain' }
    }
    if ("$($req.Headers['Authorization'])" -ne "Bearer $Token") { return @{ status = 403; code = 'AuthorizationPermissionMismatch' } }
    $slash = $path.IndexOf('/')
    if ($slash -lt 0) {
        if ($req.HttpMethod -eq 'GET' -and $q['comp'] -eq 'list') { return List-Blobs $path $q }
        return @{ status = 400; code = 'InvalidUri' }
    }
    $name = $path   # <container>/<blob>, so containers never share blobs
    switch ($req.HttpMethod) {
        'PUT' {
            if ($q['comp'] -eq 'appendblock') { return Append-Block $name $req $body }
            if ($q['comp'] -eq 'lease') { return Lease-Blob $name $req }
            return Put-Blob $name $req $body
        }
        'GET' { return Get-Blob $name $req }
        'DELETE' { return Delete-Blob $name $req $q['versionid'] }
    }
    @{ status = 405; code = 'UnsupportedHttpVerb' }
}

$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "blob stub on http://localhost:$Port/"
while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    try {
        $ms = [IO.MemoryStream]::new()
        $ctx.Request.InputStream.CopyTo($ms)
        $body = $ms.ToArray()
        if ($Log) {
            $l = [ordered]@{ method = $ctx.Request.HttpMethod; path = $ctx.Request.Url.AbsolutePath; query = $ctx.Request.Url.Query }
            [IO.File]::AppendAllText($Log, ($l | ConvertTo-Json -Compress) + "`n")
        }
        $r = Handle $ctx.Request $body
        $res = $ctx.Response
        $res.StatusCode = $r.status
        if ($r.Contains('headers')) { foreach ($k in $r.headers.Keys) { $res.Headers[$k] = $r.headers[$k] } }
        if ($r.Contains('code')) { $res.Headers['x-ms-error-code'] = $r.code }
        $out = if ($r.Contains('bytes')) { [byte[]]$r.bytes }
               elseif ($r.Contains('text')) { $res.ContentType = $r.contentType; [Text.Encoding]::UTF8.GetBytes($r.text) }
               elseif ($r.Contains('code')) { [Text.Encoding]::UTF8.GetBytes("<?xml version=`"1.0`"?><Error><Code>$($r.code)</Code></Error>") }
               else { [byte[]]@() }
        if ($r.status -ne 304) { $res.ContentLength64 = $out.Length; if ($out.Length) { $res.OutputStream.Write($out, 0, $out.Length) } }
        $res.Close()
    } catch {
        Write-Warning "blob stub: $_"
        try { $ctx.Response.StatusCode = 500; $ctx.Response.Close() } catch { }
    }
}
